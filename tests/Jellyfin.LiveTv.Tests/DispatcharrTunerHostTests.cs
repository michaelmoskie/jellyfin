using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Jellyfin.LiveTv.TunerHosts.Dispatcharr;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.LiveTv;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public class DispatcharrTunerHostTests
{
    private readonly TunerHostInfo _tunerInfo;
    private readonly DispatcharrTunerHost _host;
    private readonly Mock<HttpMessageHandler> _messageHandler;
    private int _programRequestCount;

    public DispatcharrTunerHostTests()
    {
        _tunerInfo = new TunerHostInfo
        {
            Id = "dispatcharr-test",
            Type = "dispatcharr",
            Url = "https://dispatcharr.example/base/",
            ApiKey = "test-api-key",
            ChannelProfileId = 12
        };

        _messageHandler = new Mock<HttpMessageHandler>();
        _messageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                Assert.True(request.Headers.TryGetValues("X-API-Key", out var apiKeys));
                Assert.Equal("test-api-key", Assert.Single(apiKeys));
                return Task.FromResult(CreateResponse(request.RequestUri!));
            });

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(i => i.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(_messageHandler.Object));

        var config = new Mock<IServerConfigurationManager>();
        config.Setup(i => i.GetConfiguration("livetv"))
            .Returns(new LiveTvOptions { TunerHosts = [_tunerInfo] });

        var fixture = new Fixture();
        fixture.Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(httpClientFactory);
        fixture.Inject(config.Object);
        fixture.Inject(Mock.Of<INetworkManager>());
        _host = fixture.Create<DispatcharrTunerHost>();
    }

    [Fact]
    public async Task GetChannelsAndPrograms_UsesNativeJsonAndCachesGuideWindow()
    {
        var channels = await _host.GetChannels(_tunerInfo, false, CancellationToken.None);

        Assert.Equal(2, channels.Count);
        var channel = channels.Single(i => string.Equals(i.TunerChannelId, "42", StringComparison.Ordinal));
        var secondChannel = channels.Single(i => string.Equals(i.TunerChannelId, "43", StringComparison.Ordinal));
        Assert.Equal("News One", channel.Name);
        Assert.Equal("101.5", channel.Number);
        Assert.Equal("News", channel.ChannelGroup);
        Assert.Equal("42", channel.TunerChannelId);
        Assert.Equal("dispatcharr-test", channel.TunerHostId);
        Assert.Equal("https://dispatcharr.example/base/proxy/ts/stream/channel-uuid", channel.Path);
        Assert.Equal("https://dispatcharr.example/base/api/channels/logos/5/cache/", channel.ImageUrl);
        Assert.True(_host.Supports(channel));

        var start = new DateTime(2026, 7, 27, 11, 0, 0, DateTimeKind.Utc);
        var first = (await _host.GetProgramsAsync(channel, start, start.AddDays(1), CancellationToken.None)).ToList();
        var second = (await _host.GetProgramsAsync(secondChannel, start.AddMinutes(1), start.AddHours(23), CancellationToken.None)).ToList();

        var program = Assert.Single(first);
        Assert.Equal(secondChannel.Id, Assert.Single(second).ChannelId);
        Assert.Equal(1, _programRequestCount);
        Assert.Equal("Evening News", program.Name);
        Assert.Equal("Headlines", program.EpisodeTitle);
        Assert.Equal(channel.Id, program.ChannelId);
        Assert.True(program.IsNews);
        Assert.True(program.IsLive);
        Assert.True(program.IsPremiere);
        Assert.False(program.IsRepeat);
        Assert.Equal(2, program.SeasonNumber);
        Assert.Equal(7, program.EpisodeNumber);
        Assert.Equal("TV-PG", program.OfficialRating);
        Assert.Equal("55", program.ProviderIds["Dispatcharr"]);
        Assert.StartsWith("dispatcharr-sha256-v1:", program.Etag, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_WithoutApiKey_ThrowsArgumentException()
    {
        var invalid = new TunerHostInfo
        {
            Url = "https://dispatcharr.example/"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _host.Validate(invalid));
    }

    [Fact]
    public async Task Validate_NewTunerWithoutId_Succeeds()
    {
        var newTuner = new TunerHostInfo
        {
            Type = "dispatcharr",
            Url = "https://dispatcharr.example/base/",
            ApiKey = "test-api-key",
            ChannelProfileId = 12
        };

        await _host.Validate(newTuner);
    }

    private HttpResponseMessage CreateResponse(Uri uri)
    {
        var json = uri.AbsolutePath switch
        {
            "/base/api/channels/channels/summary/" => GetChannelResponse(uri),
            "/base/api/channels/groups/" => """
                [{"id":3,"name":"News"}]
                """,
            "/base/api/epg/epgdata/" => """
                [{"id":9,"tvg_id":"guide.news","name":"News One","icon_url":null,"epg_source":7}]
                """,
            "/base/api/epg/sources/" => """
                [{"id":7,"name":"Primary Guide"}]
                """,
            "/base/api/epg/programs/search/" => GetProgramResponse(uri),
            _ => throw new InvalidOperationException($"Unexpected Dispatcharr endpoint: {uri}")
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string GetChannelResponse(Uri uri)
    {
        Assert.Contains("channel_profile_id=12", uri.Query, StringComparison.Ordinal);
        return """
            [
              {
                "id":42,
                "uuid":"channel-uuid",
                "name":"News One",
                "channel_number":101.5,
                "channel_group_id":3,
                "logo_id":5,
                "epg_data_id":9
              },
              {
                "id":43,
                "uuid":"second-channel-uuid",
                "name":"News Two",
                "channel_number":102,
                "channel_group_id":3,
                "logo_id":null,
                "epg_data_id":9
              }
            ]
            """;
    }

    private string GetProgramResponse(Uri uri)
    {
        Interlocked.Increment(ref _programRequestCount);
        var decodedQuery = Uri.UnescapeDataString(uri.Query);
        Assert.Contains("page_size=500", decodedQuery, StringComparison.Ordinal);
        Assert.Contains("start_before=", decodedQuery, StringComparison.Ordinal);
        Assert.Contains("end_after=", decodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("channels", decodedQuery, StringComparison.Ordinal);
        return """
            {
              "count":1,
              "next":null,
              "previous":null,
              "results":[{
                "id":55,
                "title":"Evening News",
                "sub_title":"Headlines",
                "description":"The day's top stories.",
                "start_time":"2026-07-27T12:00:00Z",
                "end_time":"2026-07-27T13:00:00Z",
                "tvg_id":"guide.news",
                "epg_source":"Primary Guide",
                "epg_icon_url":"https://images.example/news.png",
                "custom_properties":{
                  "categories":["news"],
                  "season":2,
                  "episode":7,
                  "live":true,
                  "premiere":true,
                  "new":true,
                  "previously_shown":true,
                  "rating":"TV-PG",
                  "date":"2026-07-27"
                }
              }]
            }
            """;
    }
}
