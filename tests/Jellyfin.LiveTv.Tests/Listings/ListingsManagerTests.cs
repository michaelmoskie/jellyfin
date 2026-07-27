using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.Listings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Listings;

public class ListingsManagerTests
{
    private readonly IConfigurationManager _config;
    private readonly IListingsProvider[] _listingsProviders;
    private readonly ILogger<ListingsManager> _logger;
    private readonly ITaskManager _taskManager;
    private readonly ITunerHostManager _tunerHostManager;

    public ListingsManagerTests()
    {
        _logger = Mock.Of<ILogger<ListingsManager>>();
        _config = Mock.Of<IConfigurationManager>();
        _taskManager = Mock.Of<ITaskManager>();
        _tunerHostManager = Mock.Of<ITunerHostManager>();
        _listingsProviders = new[] { Mock.Of<IListingsProvider>() };
    }

    [Fact]
    public void DeleteListingsProvider_DeletesProvider()
    {
        // Arrange
        var id = "MockId";
        var manager = new ListingsManager(_logger, _config, _taskManager, _tunerHostManager, _listingsProviders);

        Mock.Get(_config)
            .Setup(x => x.GetConfiguration(It.IsAny<string>()))
            .Returns(new LiveTvOptions { ListingProviders = [new ListingsProviderInfo { Id = id }] });

        // Act
        manager.DeleteListingsProvider(id);

        // Assert
        Assert.DoesNotContain(
            _config.GetLiveTvConfiguration().ListingProviders,
            p => p.Id.Equals(id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetProgramsAsync_DirectTunerGuideProvider_TakesPriority()
    {
        var channel = new ChannelInfo { Id = "dispatcharr_channel", TunerHostId = "dispatcharr" };
        var expected = new ProgramInfo { Id = "program", ChannelId = channel.Id };
        var tunerHost = new Mock<ITunerHost>();
        var directProvider = tunerHost.As<ITunerHostListingsProvider>();
        directProvider.Setup(i => i.Supports(channel)).Returns(true);
        directProvider
            .Setup(i => i.GetProgramsAsync(
                channel,
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([expected]);
        Mock.Get(_tunerHostManager)
            .SetupGet(i => i.TunerHosts)
            .Returns([tunerHost.Object]);

        var listingsProvider = new Mock<IListingsProvider>();
        var manager = new ListingsManager(
            _logger,
            _config,
            _taskManager,
            _tunerHostManager,
            [listingsProvider.Object]);

        var programs = await manager.GetProgramsAsync(
            channel,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(1),
            CancellationToken.None);

        Assert.Same(expected, Assert.Single(programs));
        listingsProvider.Verify(
            i => i.GetProgramsAsync(
                It.IsAny<ListingsProviderInfo>(),
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
