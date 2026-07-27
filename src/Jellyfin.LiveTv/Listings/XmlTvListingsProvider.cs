#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Extensions;
using Jellyfin.XmlTv;
using Jellyfin.XmlTv.Entities;
using Jellyfin.XmlTv.Enums;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Listings
{
    public class XmlTvListingsProvider : IListingsProvider
    {
        private static readonly TimeSpan _maxCacheAge = TimeSpan.FromHours(1);

        private readonly IServerConfigurationManager _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<XmlTvListingsProvider> _logger;
        private readonly Lock _programCacheLock = new();
        private XmlTvProgramCache? _programCache;

        public XmlTvListingsProvider(
            IServerConfigurationManager config,
            IHttpClientFactory httpClientFactory,
            ILogger<XmlTvListingsProvider> logger)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public string Name => "XmlTV";

        public string Type => "xmltv";

        private string GetLanguage(ListingsProviderInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.PreferredLanguage))
            {
                return info.PreferredLanguage;
            }

            return _config.Configuration.PreferredMetadataLanguage;
        }

        private async Task<string> GetXml(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            _logger.LogInformation("xmltv path: {Path}", info.Path);

            string cacheFilename = info.Id + ".xml";
            string cacheDir = Path.Join(_config.ApplicationPaths.CachePath, "xmltv");
            string cacheFile = Path.Join(cacheDir, cacheFilename);

            if (File.Exists(cacheFile))
            {
                if (File.GetLastWriteTimeUtc(cacheFile) >= DateTime.UtcNow.Subtract(_maxCacheAge))
                {
                    return cacheFile;
                }

                File.Delete(cacheFile);
            }
            else
            {
                Directory.CreateDirectory(cacheDir);
            }

            try
            {
                if (info.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Downloading xmltv listings from {Path}", info.Path);

                    using var response = await _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(info.Path, cancellationToken).ConfigureAwait(false);
                    var redirectedUrl = response.RequestMessage?.RequestUri?.ToString() ?? info.Path;
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        return await UnzipIfNeededAndCopy(redirectedUrl, stream, cacheFile, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var stream = AsyncFile.OpenRead(info.Path);
                    await using (stream.ConfigureAwait(false))
                    {
                        return await UnzipIfNeededAndCopy(info.Path, stream, cacheFile, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading or processing XMLTV file from {Path}", info.Path);

                if (File.Exists(cacheFile))
                {
                    File.Delete(cacheFile);
                }

                throw;
            }
        }

        private async Task<string> UnzipIfNeededAndCopy(string originalUrl, Stream stream, string file, CancellationToken cancellationToken)
        {
            var fileStream = new FileStream(
                file,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                IODefaults.FileStreamBufferSize,
                FileOptions.Asynchronous);

            await using (fileStream.ConfigureAwait(false))
            {
                if (Path.GetExtension(originalUrl.AsSpan().LeftPart('?')).Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(originalUrl.AsSpan().LeftPart('?')).Equals(".gzip", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new GZipStream(stream, CompressionMode.Decompress);
                        await reader.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error extracting from gz file {File}", originalUrl);
                    }
                }
                else
                {
                    await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }
            }

            var fileInfo = new FileInfo(file);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                if (fileInfo.Exists)
                {
                    File.Delete(file);
                }

                throw new InvalidOperationException("Downloaded XMLTV file is empty: " + originalUrl);
            }

            return file;
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentNullException(nameof(channelId));
            }

            _logger.LogDebug("Getting xmltv programs for channel {Id}", channelId);

            string path = await GetXml(info, cancellationToken).ConfigureAwait(false);
            var programmesByChannel = GetProgrammesByChannel(path, GetLanguage(info), startDateUtc, endDateUtc, cancellationToken);

            return programmesByChannel.TryGetValue(channelId, out var programs)
                ? programs.Select(p => GetProgramInfoWithEtag(p, info))
                : Enumerable.Empty<ProgramInfo>();
        }

        private IReadOnlyDictionary<string, IReadOnlyList<XmlTvProgram>> GetProgrammesByChannel(
            string path,
            string language,
            DateTime startDateUtc,
            DateTime endDateUtc,
            CancellationToken cancellationToken)
        {
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);

            lock (_programCacheLock)
            {
                if (_programCache?.Matches(path, lastWriteTimeUtc, language, startDateUtc, endDateUtc) == true)
                {
                    return _programCache.ProgrammesByChannel;
                }

                _logger.LogDebug("Opening XmlTvReader for {Path}", path);
                var programmesByChannel = ReadProgrammesByChannel(path, language, startDateUtc, endDateUtc, cancellationToken);
                _programCache = new XmlTvProgramCache(path, lastWriteTimeUtc, language, startDateUtc, endDateUtc, programmesByChannel);

                return programmesByChannel;
            }
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<XmlTvProgram>> ReadProgrammesByChannel(
            string path,
            string language,
            DateTime startDateUtc,
            DateTime endDateUtc,
            CancellationToken cancellationToken)
        {
            var programmesByChannel = new Dictionary<string, List<XmlTvProgram>>(StringComparer.OrdinalIgnoreCase);
            var xmlTvReader = new XmlTvReader(path, language);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true
            };

            using var reader = XmlReader.Create(path, settings);
            if (!reader.ReadToDescendant("tv") || !reader.ReadToDescendant("programme"))
            {
                return programmesByChannel.ToDictionary(i => i.Key, i => (IReadOnlyList<XmlTvProgram>)i.Value, StringComparer.OrdinalIgnoreCase);
            }

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var channelId = reader.GetAttribute("channel");
                if (string.IsNullOrWhiteSpace(channelId))
                {
                    continue;
                }

                var programme = xmlTvReader.GetProgramme(reader, channelId, startDateUtc, endDateUtc);
                if (programme is null)
                {
                    continue;
                }

                if (!programmesByChannel.TryGetValue(channelId, out var programmes))
                {
                    programmes = new List<XmlTvProgram>();
                    programmesByChannel[channelId] = programmes;
                }

                programmes.Add(programme);
            }
            while (reader.ReadToFollowing("programme"));

            return programmesByChannel.ToDictionary(i => i.Key, i => (IReadOnlyList<XmlTvProgram>)i.Value, StringComparer.OrdinalIgnoreCase);
        }

        private ProgramInfo GetProgramInfoWithEtag(XmlTvProgram program, ListingsProviderInfo info)
        {
            var programInfo = GetProgramInfo(program, info);

            if (XmlTvProgramEtag.TryCreate(programInfo, out var etag, out var reason))
            {
                programInfo.Etag = etag;
            }
            else
            {
                _logger.LogDebug(
                    "Unable to create XMLTV program ETag for program {ProgramId} on channel {ChannelId} from {StartDate} to {EndDate}: {Reason}. The program will be treated as updated on each guide refresh.",
                    programInfo.Id,
                    programInfo.ChannelId,
                    programInfo.StartDate,
                    programInfo.EndDate,
                    reason);
            }

            return programInfo;
        }

        private static ProgramInfo GetProgramInfo(XmlTvProgram program, ListingsProviderInfo info)
        {
            string? episodeTitle = program.Episode?.Title;
            var programCategories = program.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            var imageUrl = program.Icons.FirstOrDefault()?.Source;
            var episodeImageUrl = program.Images?.FirstOrDefault(m => m.Type == ImageType.Still)?.Path;
            var backgroundImageUrl = program.Images?.FirstOrDefault(m => m.Type == ImageType.Backdrop)?.Path;
            var rating = program.Ratings.FirstOrDefault()?.Value;
            var starRating = program.StarRatings?.FirstOrDefault()?.StarRating;

            var programInfo = new ProgramInfo
            {
                ChannelId = program.ChannelId,
                EndDate = program.EndDate.UtcDateTime,
                EpisodeNumber = program.Episode?.Episode,
                EpisodeTitle = episodeTitle,
                Genres = programCategories,
                StartDate = program.StartDate.UtcDateTime,
                Name = program.Title,
                Overview = program.Description,
                ProductionYear = program.CopyrightDate?.Year,
                SeasonNumber = program.Episode?.Series,
                IsSeries = program.Episode?.Episode is not null,
                IsRepeat = program.IsPreviouslyShown && !program.IsNew,
                IsPremiere = program.Premiere is not null,
                IsLive = program.IsLive,
                IsKids = programCategories.Any(c => info.KidsCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                IsMovie = programCategories.Any(c => info.MovieCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                IsNews = programCategories.Any(c => info.NewsCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                IsSports = programCategories.Any(c => info.SportsCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                ImageUrl = string.IsNullOrEmpty(imageUrl) ? null : imageUrl,
                HasImage = !string.IsNullOrEmpty(imageUrl),
                BackdropImageUrl = string.IsNullOrEmpty(backgroundImageUrl) ? null : backgroundImageUrl,
                ThumbImageUrl = string.IsNullOrEmpty(episodeImageUrl) ? null : episodeImageUrl,
                OfficialRating = string.IsNullOrEmpty(rating) ? null : rating,
                CommunityRating = starRating is null ? null : (float)starRating.Value,
                SeriesId = program.Episode?.Episode is null ? null : program.Title?.GetMD5().ToString("N", CultureInfo.InvariantCulture)
            };

            if (string.IsNullOrWhiteSpace(program.ProgramId))
            {
                string uniqueString = (program.Title ?? string.Empty) + (episodeTitle ?? string.Empty);

                if (programInfo.SeasonNumber.HasValue)
                {
                    uniqueString = "-" + programInfo.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (programInfo.EpisodeNumber.HasValue)
                {
                    uniqueString = "-" + programInfo.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture);
                }

                programInfo.ShowId = uniqueString.GetMD5().ToString("N", CultureInfo.InvariantCulture);

                // If we don't have valid episode info, assume it's a unique program, otherwise recordings might be skipped
                if (programInfo.IsSeries
                    && !programInfo.IsRepeat
                    && (programInfo.EpisodeNumber ?? 0) == 0)
                {
                    programInfo.ShowId += programInfo.StartDate.Ticks.ToString(CultureInfo.InvariantCulture);
                }
            }
            else
            {
                programInfo.ShowId = program.ProgramId;
            }

            // Construct an id from the channel and start date
            programInfo.Id = string.Format(CultureInfo.InvariantCulture, "{0}_{1:O}", program.ChannelId, program.StartDate);

            if (programInfo.IsMovie)
            {
                programInfo.IsSeries = false;
                programInfo.EpisodeNumber = null;
                programInfo.EpisodeTitle = null;
            }

            return programInfo;
        }

        public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        {
            // Assume all urls are valid. check files for existence
            if (!info.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(info.Path))
            {
                throw new FileNotFoundException("Could not find the XmlTv file specified:", info.Path);
            }

            return Task.CompletedTask;
        }

        public async Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        {
            // In theory this should never be called because there is always only one lineup
            string path = await GetXml(info, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("Opening XmlTvReader for {Path}", path);
            var reader = new XmlTvReader(path, GetLanguage(info));
            IEnumerable<XmlTvChannel> results = reader.GetChannels();

            // Should this method be async?
            return results.Select(c => new NameIdPair() { Id = c.Id, Name = c.DisplayName }).ToList();
        }

        public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            // In theory this should never be called because there is always only one lineup
            string path = await GetXml(info, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Opening XmlTvReader for {Path}", path);
            var reader = new XmlTvReader(path, GetLanguage(info));
            var results = reader.GetChannels();

            // Should this method be async?
            return results.Select(c => new ChannelInfo
            {
                Id = c.Id,
                Name = c.DisplayName,
                ImageUrl = string.IsNullOrEmpty(c.Icons.FirstOrDefault()?.Source) ? null : c.Icons.FirstOrDefault()!.Source,
                Number = string.IsNullOrWhiteSpace(c.Number) ? c.Id : c.Number
            }).ToList();
        }

        private sealed class XmlTvProgramCache
        {
            public XmlTvProgramCache(
                string path,
                DateTime lastWriteTimeUtc,
                string language,
                DateTime startDateUtc,
                DateTime endDateUtc,
                IReadOnlyDictionary<string, IReadOnlyList<XmlTvProgram>> programmesByChannel)
            {
                Path = path;
                LastWriteTimeUtc = lastWriteTimeUtc;
                Language = language;
                StartDateUtc = startDateUtc;
                EndDateUtc = endDateUtc;
                ProgrammesByChannel = programmesByChannel;
            }

            public string Path { get; }

            public DateTime LastWriteTimeUtc { get; }

            public string Language { get; }

            public DateTime StartDateUtc { get; }

            public DateTime EndDateUtc { get; }

            public IReadOnlyDictionary<string, IReadOnlyList<XmlTvProgram>> ProgrammesByChannel { get; }

            public bool Matches(string path, DateTime lastWriteTimeUtc, string language, DateTime startDateUtc, DateTime endDateUtc)
                => string.Equals(Path, path, StringComparison.Ordinal)
                   && LastWriteTimeUtc == lastWriteTimeUtc
                   && string.Equals(Language, language, StringComparison.OrdinalIgnoreCase)
                   && StartDateUtc == startDateUtc
                   && EndDateUtc == endDateUtc;
        }
    }
}
