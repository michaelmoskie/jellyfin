using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using Jellyfin.LiveTv.Listings;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts.Dispatcharr;

/// <summary>
/// Imports Dispatcharr channels and guide data through its native JSON API.
/// </summary>
public sealed class DispatcharrTunerHost : M3UTunerHost, ITunerHostListingsProvider
{
    private const string ApiKeyHeader = "X-API-Key";
    private const int ProgramPageSize = 500;

    private static readonly TimeSpan _metadataCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _guideCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _guideWindowPadding = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly string[] _kidsCategories = ["kids", "family", "children", "childrens", "disney"];
    private static readonly string[] _movieCategories = ["movie", "movies", "film"];
    private static readonly string[] _newsCategories = ["news", "journalism", "current affairs"];
    private static readonly string[] _sportsCategories = ["sports", "sport", "basketball", "baseball", "football", "hockey", "soccer"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, HostMetadata> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GuideCache> _guideCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _metadataLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _guideLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatcharrTunerHost"/> class.
    /// </summary>
    /// <param name="config">The server configuration manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="appHost">The server application host.</param>
    /// <param name="networkManager">The network manager.</param>
    /// <param name="streamHelper">The live stream helper.</param>
    public DispatcharrTunerHost(
        IServerConfigurationManager config,
        IMediaSourceManager mediaSourceManager,
        ILogger<M3UTunerHost> logger,
        IFileSystem fileSystem,
        IHttpClientFactory httpClientFactory,
        IServerApplicationHost appHost,
        INetworkManager networkManager,
        IStreamHelper streamHelper)
        : base(
            config,
            mediaSourceManager,
            logger,
            fileSystem,
            httpClientFactory,
            appHost,
            networkManager,
            streamHelper)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override string Type => "dispatcharr";

    /// <inheritdoc />
    public override string Name => "Dispatcharr";

    /// <inheritdoc />
    public bool Supports(ChannelInfo channel)
        => channel.Id?.StartsWith(ChannelIdPrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <inheritdoc />
    public override async Task Validate(TunerHostInfo info)
    {
        ValidateConfiguration(info);
        _ = await LoadMetadataAsync(info, true, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        ChannelInfo channel,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        var tuner = GetTunerHosts().FirstOrDefault(
            i => string.Equals(i.Id, channel.TunerHostId, StringComparison.OrdinalIgnoreCase));
        if (tuner is null)
        {
            return [];
        }

        var metadata = await LoadMetadataAsync(tuner, false, cancellationToken).ConfigureAwait(false);
        var guide = await LoadGuideAsync(tuner, metadata, startDateUtc, endDateUtc, cancellationToken).ConfigureAwait(false);
        var rawChannelId = channel.TunerChannelId;
        if (string.IsNullOrWhiteSpace(rawChannelId))
        {
            return [];
        }

        IReadOnlyList<DispatcharrProgramDto>? programs = null;
        if (metadata.ChannelGuideKeys.TryGetValue(rawChannelId, out var guideKey))
        {
            guide.ProgramsByGuideKey.TryGetValue(guideKey, out programs);
        }

        if (programs is null)
        {
            return [];
        }

        return programs
            .Where(i => i.EndTime.UtcDateTime > startDateUtc && i.StartTime.UtcDateTime < endDateUtc)
            .Select(i => MapProgram(tuner, channel, i))
            .ToList();
    }

    /// <inheritdoc />
    protected override async Task<List<ChannelInfo>> GetChannelsInternal(
        TunerHostInfo tuner,
        CancellationToken cancellationToken)
    {
        var metadata = await LoadMetadataAsync(tuner, true, cancellationToken).ConfigureAwait(false);
        var channelIdPrefix = ChannelIdPrefix
            + GetBaseUri(tuner.Url).AbsoluteUri.GetMD5().ToString("N", CultureInfo.InvariantCulture)
            + "_";

        return metadata.Channels.Select(channel =>
        {
            var rawChannelId = channel.Id.ToString(CultureInfo.InvariantCulture);
            metadata.GroupNames.TryGetValue(channel.EffectiveChannelGroupId ?? 0, out var groupName);

            return new ChannelInfo
            {
                Id = channelIdPrefix + rawChannelId,
                TunerChannelId = rawChannelId,
                TunerHostId = tuner.Id,
                Name = channel.EffectiveName ?? rawChannelId,
                Number = channel.EffectiveChannelNumber?.ToString("0.################", CultureInfo.InvariantCulture),
                ChannelType = ChannelType.TV,
                ChannelGroup = groupName,
                Tags = string.IsNullOrWhiteSpace(groupName) ? [] : [groupName],
                ImageUrl = channel.EffectiveLogoId.HasValue
                    ? BuildEndpointUri(tuner.Url, $"api/channels/logos/{channel.EffectiveLogoId.Value}/cache/").AbsoluteUri
                    : null,
                HasImage = channel.EffectiveLogoId.HasValue,
                Path = BuildEndpointUri(tuner.Url, $"proxy/ts/stream/{Uri.EscapeDataString(channel.Uuid)}").AbsoluteUri
            };
        }).ToList();
    }

    private async Task<HostMetadata> LoadMetadataAsync(
        TunerHostInfo tuner,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration(tuner);
        var cacheKey = GetCacheKey(tuner);

        if (!forceRefresh
            && _metadataCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached;
        }

        var cacheLock = _metadataLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh
                && _metadataCache.TryGetValue(cacheKey, out cached)
                && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached;
            }

            var profileQuery = tuner.ChannelProfileId is > 0
                ? $"?channel_profile_id={tuner.ChannelProfileId.Value.ToString(CultureInfo.InvariantCulture)}"
                : string.Empty;

            var channelsTask = GetJsonAsync<List<DispatcharrChannelDto>>(
                tuner,
                BuildEndpointUri(tuner.Url, "api/channels/channels/summary/" + profileQuery),
                cancellationToken);
            var groupsTask = GetJsonAsync<List<DispatcharrGroupDto>>(
                tuner,
                BuildEndpointUri(tuner.Url, "api/channels/groups/"),
                cancellationToken);
            var epgDataTask = GetJsonAsync<List<DispatcharrEpgDataDto>>(
                tuner,
                BuildEndpointUri(tuner.Url, "api/epg/epgdata/"),
                cancellationToken);
            var epgSourcesTask = GetJsonAsync<List<DispatcharrEpgSourceDto>>(
                tuner,
                BuildEndpointUri(tuner.Url, "api/epg/sources/"),
                cancellationToken);

            await Task.WhenAll(channelsTask, groupsTask, epgDataTask, epgSourcesTask).ConfigureAwait(false);

            var channels = await channelsTask.ConfigureAwait(false);
            var groups = await groupsTask.ConfigureAwait(false);
            var epgData = await epgDataTask.ConfigureAwait(false);
            var epgSources = await epgSourcesTask.ConfigureAwait(false);
            var sourceIds = epgSources
                .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                .GroupBy(i => i.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(i => i.Key, i => i.First().Id, StringComparer.OrdinalIgnoreCase);
            var epgKeys = epgData.ToDictionary(
                i => i.Id,
                i => new GuideKey(i.EpgSourceId, i.TvgId ?? string.Empty));
            var channelGuideKeys = new Dictionary<string, GuideKey>(StringComparer.OrdinalIgnoreCase);

            foreach (var channel in channels)
            {
                if (channel.EffectiveEpgDataId.HasValue
                    && epgKeys.TryGetValue(channel.EffectiveEpgDataId.Value, out var key))
                {
                    channelGuideKeys[channel.Id.ToString(CultureInfo.InvariantCulture)] = key;
                }
            }

            cached = new HostMetadata(
                DateTimeOffset.UtcNow.Add(_metadataCacheDuration),
                channels,
                groups.ToDictionary(i => i.Id, i => i.Name ?? string.Empty),
                channelGuideKeys,
                sourceIds);
            _metadataCache[cacheKey] = cached;
            return cached;
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private async Task<GuideCache> LoadGuideAsync(
        TunerHostInfo tuner,
        HostMetadata metadata,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        var cacheKey = GetCacheKey(tuner);
        if (_guideCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow
            && cached.StartDateUtc <= startDateUtc
            && cached.EndDateUtc >= endDateUtc)
        {
            return cached;
        }

        var cacheLock = _guideLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_guideCache.TryGetValue(cacheKey, out cached)
                && cached.ExpiresAt > DateTimeOffset.UtcNow
                && cached.StartDateUtc <= startDateUtc
                && cached.EndDateUtc >= endDateUtc)
            {
                return cached;
            }

            var fetchStart = DateTime.SpecifyKind(startDateUtc, DateTimeKind.Utc).Subtract(_guideWindowPadding);
            var fetchEnd = DateTime.SpecifyKind(endDateUtc, DateTimeKind.Utc).Add(_guideWindowPadding);
            var programs = await GetProgramPagesAsync(tuner, fetchStart, fetchEnd, cancellationToken).ConfigureAwait(false);
            var byGuideKey = new Dictionary<GuideKey, List<DispatcharrProgramDto>>();

            foreach (var program in programs)
            {
                var sourceId = !string.IsNullOrWhiteSpace(program.EpgSource)
                    && metadata.SourceIds.TryGetValue(program.EpgSource, out var resolvedSourceId)
                    ? resolvedSourceId
                    : 0;
                var key = new GuideKey(sourceId, program.TvgId ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(key.TvgId))
                {
                    AddProgram(byGuideKey, key, program);
                }
            }

            cached = new GuideCache(
                DateTimeOffset.UtcNow.Add(_guideCacheDuration),
                fetchStart,
                fetchEnd,
                byGuideKey.ToDictionary(i => i.Key, i => (IReadOnlyList<DispatcharrProgramDto>)i.Value));
            _guideCache[cacheKey] = cached;
            return cached;
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private async Task<List<DispatcharrProgramDto>> GetProgramPagesAsync(
        TunerHostInfo tuner,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        var programs = new List<DispatcharrProgramDto>();
        var fields = "id,title,sub_title,description,start_time,end_time,tvg_id,custom_properties,epg_source,epg_icon_url";
        var page = 1;
        var totalCount = int.MaxValue;

        while (programs.Count < totalCount)
        {
            var query = string.Create(
                CultureInfo.InvariantCulture,
                $"?start_before={Uri.EscapeDataString(endDateUtc.ToString("O", CultureInfo.InvariantCulture))}"
                + $"&end_after={Uri.EscapeDataString(startDateUtc.ToString("O", CultureInfo.InvariantCulture))}"
                + $"&fields={Uri.EscapeDataString(fields)}"
                + $"&page_size={ProgramPageSize}"
                + $"&page={page}");
            var response = await GetJsonAsync<DispatcharrProgramPageDto>(
                tuner,
                BuildEndpointUri(tuner.Url, "api/epg/programs/search/" + query),
                cancellationToken).ConfigureAwait(false);

            totalCount = response.Count;
            if (response.Results.Count == 0)
            {
                break;
            }

            programs.AddRange(response.Results);
            page++;
        }

        Logger.LogInformation(
            "Loaded {ProgramCount} programs from Dispatcharr for {StartDateUtc} through {EndDateUtc}",
            programs.Count,
            startDateUtc,
            endDateUtc);

        return programs;
    }

    private async Task<T> GetJsonAsync<T>(
        TunerHostInfo tuner,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, tuner.ApiKey);
        if (!string.IsNullOrWhiteSpace(tuner.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", tuner.UserAgent);
        }

        using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Dispatcharr returned an empty JSON response from {endpoint.AbsolutePath}.");
    }

    private ProgramInfo MapProgram(
        TunerHostInfo tuner,
        ChannelInfo channel,
        DispatcharrProgramDto program)
    {
        var categories = GetStringList(program.CustomProperties, "categories");
        var seasonNumber = GetNullableInt(program.CustomProperties, "season");
        var episodeNumber = GetNullableInt(program.CustomProperties, "episode");
        var episodeTitle = string.IsNullOrWhiteSpace(program.SubTitle) ? null : program.SubTitle;
        var isMovie = HasCategory(categories, _movieCategories);
        var isSeries = !isMovie && (seasonNumber.HasValue || episodeNumber.HasValue || episodeTitle is not null);
        var icon = GetString(program.CustomProperties, "icon");
        var imageUrl = ResolveOptionalUri(tuner.Url, icon ?? program.EpgIconUrl);
        var originalAirDate = GetNullableDateTime(program.CustomProperties, "date");
        var title = program.Title ?? string.Empty;
        var seriesId = isSeries
            ? title.GetMD5().ToString("N", CultureInfo.InvariantCulture)
            : null;
        var showKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{title}|{episodeTitle}|{seasonNumber}|{episodeNumber}");

        var result = new ProgramInfo
        {
            Id = $"dispatcharr_{tuner.Id}_{program.GetId()}_{channel.TunerChannelId}",
            ChannelId = channel.Id,
            Name = title,
            EpisodeTitle = episodeTitle,
            Overview = program.Description,
            StartDate = program.StartTime.UtcDateTime,
            EndDate = program.EndTime.UtcDateTime,
            Genres = categories,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            IsSeries = isSeries,
            IsMovie = isMovie,
            IsKids = HasCategory(categories, _kidsCategories),
            IsNews = HasCategory(categories, _newsCategories),
            IsSports = HasCategory(categories, _sportsCategories),
            IsLive = GetBoolean(program.CustomProperties, "live"),
            IsPremiere = GetBoolean(program.CustomProperties, "premiere"),
            IsRepeat = GetBoolean(program.CustomProperties, "previously_shown")
                && !GetBoolean(program.CustomProperties, "new"),
            OfficialRating = GetString(program.CustomProperties, "rating"),
            OriginalAirDate = originalAirDate,
            ProductionYear = originalAirDate?.Year,
            ImageUrl = imageUrl,
            HasImage = imageUrl is not null,
            SeriesId = seriesId,
            ShowId = showKey.GetMD5().ToString("N", CultureInfo.InvariantCulture)
        };

        result.ProviderIds["Dispatcharr"] = program.GetId();
        if (DispatcharrProgramEtag.TryCreate(result, out var etag, out var reason))
        {
            result.Etag = etag;
        }
        else
        {
            Logger.LogDebug(
                "Unable to create Dispatcharr program ETag for program {ProgramId}: {Reason}",
                program.GetId(),
                reason);
        }

        return result;
    }

    private static void ValidateConfiguration(TunerHostInfo info)
    {
        _ = GetBaseUri(info.Url);
        if (string.IsNullOrWhiteSpace(info.ApiKey))
        {
            throw new ArgumentException("A Dispatcharr API key is required.", nameof(info));
        }
    }

    private static Uri GetBaseUri(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Dispatcharr URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = uri.AbsolutePath.TrimEnd('/') + "/"
        };
        return builder.Uri;
    }

    private static Uri BuildEndpointUri(string url, string relativePath)
        => new(GetBaseUri(url), relativePath);

    private static string GetCacheKey(TunerHostInfo tuner)
    {
        if (!string.IsNullOrWhiteSpace(tuner.Id))
        {
            return tuner.Id;
        }

        var unsavedIdentity = string.Create(
            CultureInfo.InvariantCulture,
            $"{GetBaseUri(tuner.Url).AbsoluteUri}|{tuner.ChannelProfileId}");
        return "unsaved_" + unsavedIdentity.GetMD5().ToString("N", CultureInfo.InvariantCulture);
    }

    private static string? ResolveOptionalUri(string baseUrl, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.AbsoluteUri
            : new Uri(GetBaseUri(baseUrl), value.TrimStart('/')).AbsoluteUri;
    }

    private static void AddProgram<TKey>(
        Dictionary<TKey, List<DispatcharrProgramDto>> programs,
        TKey key,
        DispatcharrProgramDto program)
        where TKey : notnull
    {
        if (!programs.TryGetValue(key, out var list))
        {
            list = [];
            programs[key] = list;
        }

        list.Add(program);
    }

    private static bool HasCategory(IReadOnlyList<string> categories, IReadOnlyList<string> matches)
        => categories.Any(category => matches.Contains(category, StringComparer.OrdinalIgnoreCase));

    private static List<string> GetStringList(JsonElement? properties, string name)
    {
        if (!TryGetProperty(properties, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(i => i.ValueKind == JsonValueKind.String)
            .Select(i => i.GetString())
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Cast<string>()
            .ToList();
    }

    private static string? GetString(JsonElement? properties, string name)
        => TryGetProperty(properties, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetNullableInt(JsonElement? properties, string name)
    {
        if (!TryGetProperty(properties, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static bool GetBoolean(JsonElement? properties, string name)
    {
        if (!TryGetProperty(properties, name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var boolean) && boolean,
            _ => false
        };
    }

    private static DateTime? GetNullableDateTime(JsonElement? properties, string name)
        => TryGetProperty(properties, name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date)
                ? date
                : null;

    private static bool TryGetProperty(JsonElement? properties, string name, out JsonElement value)
    {
        if (properties.HasValue
            && properties.Value.ValueKind == JsonValueKind.Object
            && properties.Value.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private readonly record struct GuideKey(long SourceId, string TvgId);

    private sealed record HostMetadata(
        DateTimeOffset ExpiresAt,
        IReadOnlyList<DispatcharrChannelDto> Channels,
        IReadOnlyDictionary<long, string> GroupNames,
        IReadOnlyDictionary<string, GuideKey> ChannelGuideKeys,
        IReadOnlyDictionary<string, long> SourceIds);

    private sealed record GuideCache(
        DateTimeOffset ExpiresAt,
        DateTime StartDateUtc,
        DateTime EndDateUtc,
        IReadOnlyDictionary<GuideKey, IReadOnlyList<DispatcharrProgramDto>> ProgramsByGuideKey);

    private sealed class DispatcharrChannelDto
    {
        public long Id { get; set; }

        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? EffectiveName { get; set; }

        [JsonPropertyName("channel_number")]
        public double? EffectiveChannelNumber { get; set; }

        [JsonPropertyName("channel_group_id")]
        public long? EffectiveChannelGroupId { get; set; }

        [JsonPropertyName("logo_id")]
        public long? EffectiveLogoId { get; set; }

        [JsonPropertyName("epg_data_id")]
        public long? EffectiveEpgDataId { get; set; }
    }

    private sealed class DispatcharrGroupDto
    {
        public long Id { get; set; }

        public string? Name { get; set; }
    }

    private sealed class DispatcharrEpgDataDto
    {
        public long Id { get; set; }

        [JsonPropertyName("tvg_id")]
        public string? TvgId { get; set; }

        [JsonPropertyName("epg_source")]
        public long EpgSourceId { get; set; }
    }

    private sealed class DispatcharrEpgSourceDto
    {
        public long Id { get; set; }

        public string? Name { get; set; }
    }

    private sealed class DispatcharrProgramPageDto
    {
        public int Count { get; set; }

        public List<DispatcharrProgramDto> Results { get; set; } = [];
    }

    private sealed class DispatcharrProgramDto
    {
        public JsonElement Id { get; set; }

        public string? Title { get; set; }

        [JsonPropertyName("sub_title")]
        public string? SubTitle { get; set; }

        public string? Description { get; set; }

        [JsonPropertyName("start_time")]
        public DateTimeOffset StartTime { get; set; }

        [JsonPropertyName("end_time")]
        public DateTimeOffset EndTime { get; set; }

        [JsonPropertyName("tvg_id")]
        public string? TvgId { get; set; }

        [JsonPropertyName("custom_properties")]
        public JsonElement? CustomProperties { get; set; }

        [JsonPropertyName("epg_source")]
        public string? EpgSource { get; set; }

        [JsonPropertyName("epg_icon_url")]
        public string? EpgIconUrl { get; set; }

        public string GetId()
            => Id.ValueKind == JsonValueKind.String
                ? Id.GetString() ?? string.Empty
                : Id.GetRawText();
    }
}
