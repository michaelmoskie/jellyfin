# Dispatcharr Live TV source

The Dispatcharr tuner is a native Jellyfin Live TV source. Dispatcharr remains
the system of record for channel ordering, groups, logos, channel profiles, EPG
matching, and stream failover. Jellyfin owns the guide UI, programme library,
search, timers, recording, and playback pipeline.

Unlike the HDHomeRun/M3U plus XMLTV setup, this source does not download or
parse M3U or XMLTV data. It reads Dispatcharr's effective channel summary and
bulk programme-search JSON APIs. Guide pages are downloaded once per refresh
window at the API's maximum page size, projected to programme fields only,
grouped by the authoritative EPG source and TVG ID, and reused for every
Jellyfin channel. Stable programme ETags keep unchanged guide rows out of
Jellyfin's update path. Dispatcharr's TS relay is used as the live stream
input; its browser player is not used.

## Configuration

Create a Dispatcharr API key for a Standard or Admin user. The user and
Dispatcharr network-access rules determine which API data Jellyfin may read.
In Jellyfin, open **Dashboard → Live TV**, add a tuner device, and select
**Dispatcharr**. Enter the Dispatcharr URL and API key. The optional channel
profile ID limits the import to enabled members of that profile.

The equivalent server configuration is:

```json
{
  "Type": "dispatcharr",
  "Url": "http://dispatcharr:9191/",
  "ApiKey": "replace-with-a-dispatcharr-api-key",
  "ChannelProfileId": null,
  "TunerCount": 0,
  "AllowStreamSharing": true
}
```

`ChannelProfileId` is optional. When present, only enabled members of that
Dispatcharr channel profile are imported. The URL may include a reverse-proxy
subpath. Match channels to EPG data in Dispatcharr; Jellyfin intentionally uses
that effective EPG assignment instead of performing its own channel matching.

The source validates the API key and required endpoints when it is saved.
Dispatcharr must allow Jellyfin to access both its UI/API network policy and
the `/proxy/ts/stream/{uuid}` relay path.

## Dispatcharr API contract

The integration consumes these endpoints:

- `/api/channels/channels/summary/`
- `/api/channels/groups/`
- `/api/channels/logos/{id}/cache/`
- `/api/epg/epgdata/`
- `/api/epg/sources/`
- `/api/epg/programs/search/`
- `/proxy/ts/stream/{uuid}`

Programme search must support overlap filters (`start_before` and `end_after`),
field projection, and page sizes up to 500. Channel summary values are the
effective values after Dispatcharr overrides, so channel changes apply on the
next Jellyfin guide refresh without an intermediate export or parse.
