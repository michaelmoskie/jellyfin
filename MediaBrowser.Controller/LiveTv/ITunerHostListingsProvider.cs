using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Provides guide data directly from a tuner host without a separate listings provider.
/// </summary>
public interface ITunerHostListingsProvider
{
    /// <summary>
    /// Determines whether this provider supplies guide data for a channel.
    /// </summary>
    /// <param name="channel">The tuner channel.</param>
    /// <returns><see langword="true"/> when this provider owns the channel.</returns>
    bool Supports(ChannelInfo channel);

    /// <summary>
    /// Gets guide programs for a tuner channel.
    /// </summary>
    /// <param name="channel">The tuner channel.</param>
    /// <param name="startDateUtc">The beginning of the guide window.</param>
    /// <param name="endDateUtc">The end of the guide window.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The programs in the requested guide window.</returns>
    Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        ChannelInfo channel,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken);
}
