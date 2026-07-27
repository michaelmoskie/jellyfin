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

After completing Jellyfin's first-run wizard, open **Dashboard → Live TV**,
add a tuner device, and select **Dispatcharr**. Enter:

- Dispatcharr URL: `http://dispatcharr:9191/`
- Dispatcharr API key: the key created in Dispatcharr
- Dispatcharr channel profile ID: optional; leave blank to import all channels

The API key is displayed as a password field. Existing Dispatcharr tuners can
be edited from the same page. No Jellyfin API key or manual `curl` request is
needed.

The script is safe to rerun. Use `--no-build` to restart existing images
without recompiling Jellyfin.
