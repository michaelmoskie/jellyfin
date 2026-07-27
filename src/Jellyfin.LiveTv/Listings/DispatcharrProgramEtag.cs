using System;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.Listings;

internal static class DispatcharrProgramEtag
{
    internal const string Prefix = "dispatcharr-sha256-v1:";

    internal static bool MatchesStored(string? incomingEtag, string? storedEtag)
        => !string.IsNullOrWhiteSpace(incomingEtag)
            && incomingEtag.StartsWith(Prefix, StringComparison.Ordinal)
            && string.Equals(incomingEtag, storedEtag, StringComparison.OrdinalIgnoreCase);

    internal static bool TryCreate(ProgramInfo programInfo, out string? etag, out string? reason)
    {
        if (!XmlTvProgramEtag.TryCreate(programInfo, out var fieldHash, out reason))
        {
            etag = null;
            return false;
        }

        etag = Prefix + fieldHash![XmlTvProgramEtag.Prefix.Length..];
        return true;
    }
}
