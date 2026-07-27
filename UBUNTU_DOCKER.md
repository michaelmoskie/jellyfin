# Ubuntu Docker deployment

This deployment builds the current Jellyfin server source and the production
web client from `michaelmoskie/jellyfin-web`, pulls Dispatcharr, and starts both
services on an Ubuntu host's `192.168.1.x` address.

## Requirements

- Docker Engine
- Docker Compose v2 (`docker compose`)
- `curl`
- `ip` from `iproute2`
- A host address in `192.168.1.0/24`
- Internet access while building and pulling images

Run the deployment script from the repository:

```bash
chmod +x scripts/start-ubuntu-compose.sh
./scripts/start-ubuntu-compose.sh
```

The script detects the first `192.168.1.x` address. To select one explicitly:

```bash
./scripts/start-ubuntu-compose.sh --lan-ip 192.168.1.20
```

It writes `.env.ubuntu`, creates persistent directories under `run/`, validates
the Compose model, builds Jellyfin and jellyfin-web, starts Dispatcharr, and
waits for both HTTP health endpoints.

The published ports bind only to the selected LAN address:

- Jellyfin: `http://192.168.1.X:8096`
- Dispatcharr: `http://192.168.1.X:9191`
- Jellyfin discovery: UDP 7359

Do not configure router port-forwarding for these ports. If Ubuntu has an
additional host firewall, allow TCP 8096 and 9191 plus UDP 7359 only from
`192.168.1.0/24`.

For UFW, the corresponding rules are:

```bash
sudo ufw allow from 192.168.1.0/24 to any port 8096 proto tcp
sudo ufw allow from 192.168.1.0/24 to any port 9191 proto tcp
sudo ufw allow from 192.168.1.0/24 to any port 7359 proto udp
```

## Dispatcharr setup

Complete Dispatcharr's first-run setup, create an API key for a Standard or
Admin user, and ensure its network-access settings allow the Compose network.
Jellyfin reaches it internally at `http://dispatcharr:9191/`.

Add the native Dispatcharr tuner to Jellyfin using the fields documented in
`src/Jellyfin.LiveTv/TunerHosts/Dispatcharr/README.md`. The current web client
does not expose the API key and channel-profile fields in its tuner form, so
use Jellyfin's `POST /LiveTv/TunerHosts` API:

```json
{
  "Type": "dispatcharr",
  "Url": "http://dispatcharr:9191/",
  "ApiKey": "replace-with-the-dispatcharr-api-key",
  "ChannelProfileId": null,
  "TunerCount": 0,
  "AllowStreamSharing": true
}
```

After completing Jellyfin's first-run wizard and creating a Jellyfin API key,
the tuner can be added from the Ubuntu host with:

```bash
curl --fail-with-body \
  --request POST \
  --header 'Content-Type: application/json' \
  --header 'X-Emby-Token: REPLACE_WITH_JELLYFIN_API_KEY' \
  --data '{
    "Type": "dispatcharr",
    "Url": "http://dispatcharr:9191/",
    "ApiKey": "REPLACE_WITH_DISPATCHARR_API_KEY",
    "ChannelProfileId": null,
    "TunerCount": 0,
    "AllowStreamSharing": true
  }' \
  http://192.168.1.X:8096/LiveTv/TunerHosts
```

The script is safe to rerun. Use `--no-build` to restart existing images
without recompiling Jellyfin.
