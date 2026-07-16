# PlexRequests

A self-hosted, Overseerr-style request portal for your Plex server. Your friends sign in with their
own Plex account, browse movies and TV (powered by TMDB), and request titles for your shared library.
You approve or deny from an admin dashboard, and — if you wire up the optional downloader — approved
requests are fetched, sorted into your library, and marked **Available** automatically.

Built with Blazor Server (.NET 10), MudBlazor, EF Core + SQLite. Ships as Docker containers.

- **Request portal** — Plex sign-in, TMDB-powered browse/search, watchlist, per-user request limits.
- **Admin dashboard** — approve/deny, manage users, see request status (Pending → Approved →
  Downloading → Available / Failed).
- **Notifications** — in-app, and optionally to Discord via the companion bridge extension.
- **Optional automation** — an out-of-process downloader that searches indexers, ranks releases,
  drives a torrent client (Deluge/qBittorrent), hardlinks into your library, and reindexes Plex.
- **Optional VPN** — run the torrent stack behind a gluetun kill-switch, or bring your own.

---

## Table of contents

1. [What you need before you start](#1-what-you-need-before-you-start)
2. [Getting your keys](#2-getting-your-keys)
3. [Quick start (Docker)](#3-quick-start-docker)
4. [First run — become the admin](#4-first-run--become-the-admin)
5. [Exposing it publicly (Cloudflare Tunnel / nginx)](#5-exposing-it-publicly)
6. [Optional: automatic downloading](#6-optional-automatic-downloading)
7. [Optional: Discord bridge](#7-optional-discord-bridge)
8. [Configuration reference](#8-configuration-reference)
9. [Backups & data](#9-backups--data)
10. [Running from source (development)](#10-running-from-source-development)
11. [Troubleshooting](#11-troubleshooting)

---

## 1. What you need before you start

- A **Plex Media Server** you own/admin, reachable from where you deploy this (LAN IP or public).
- **Docker** and **Docker Compose** on the host (Linux recommended). That's the only runtime
  dependency — you do **not** need .NET installed to run the containers.
- A free **TMDB** account (for artwork and metadata).
- *(Optional)* A domain + Cloudflare account if you want friends to reach it from the internet.
- *(Optional)* A torrent client and/or VPN if you want automatic fulfillment.

---

## 2. Getting your keys

You need three things at minimum: a **TMDB API key**, your **Plex server URL**, and a **Plex token**.

### TMDB API key (required — metadata & artwork)

1. Create a free account at <https://www.themoviedb.org/signup>.
2. Go to **Settings → API** (<https://www.themoviedb.org/settings/api>) and request an **API Key**
   (choose "Developer"; you can put anything reasonable for the app details — it's for personal use).
3. Copy the **API Key (v3 auth)** string. That's your `TMDB_API_KEY`.

### Plex server URL (required)

The address of your Plex server, including the port (default `32400`):

- Same machine / LAN: `http://192.168.1.50:32400`
- Remote/public Plex: `http://your-public-ip:32400` (or an https reverse-proxied URL).

This becomes `PLEX_URL`. If your Plex uses a self-signed HTTPS cert, also set
`PLEX_ALLOW_INVALID_CERTS=true`.

### Plex token (required — lets the app read your library for "already available" checks)

This is a server-owner token used to query your library. To find it:

1. Sign in to Plex Web (<https://app.plex.tv>).
2. Play any item → click the **⋮** → **Get Info** → **View XML**.
3. In the URL that opens, copy the value of `X-Plex-Token=...`.

Full instructions: <https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/>

This becomes `PLEX_TOKEN`.

> ⚠️ **Treat the Plex token like a password.** Anyone with it can access your Plex account. Keep it
> only in your `.env` (which is gitignored) and rotate it if it's ever exposed.

### Admin username (required — who gets the admin dashboard)

Set `ADMIN_USERNAMES` to your **Plex username** (comma-separated for multiple admins). When you sign
in with a matching Plex account, you're automatically granted the Admin role. Everyone else is a
normal user.

### Fulfillment / Bridge keys (optional — only if you use those features)

- `FULFILLMENT_API_KEY` — a long random shared secret between the web app and the downloader. Generate
  one with `openssl rand -hex 32`. Only needed if you run the downloader.
- `BRIDGE_API_KEY` — same idea, shared secret for the Discord bridge extension. Only needed if you use
  Discord. Set `BRIDGE_ENABLED=true` alongside it.

---

## 3. Quick start (Docker)

```bash
git clone <your-fork-url> PlexRequests
cd PlexRequests

# Create your .env from the template and fill it in
cp docker/.env.example .env
nano .env        # set TMDB_API_KEY, ADMIN_USERNAMES, PLEX_URL, PLEX_TOKEN (see §2)

# Start the web app (+ downloader; the torrent client stays off by default)
docker compose up -d
```

The portal is now on **<http://localhost:8080>** (or `http://<host-ip>:8080` on your LAN).

A minimal `.env` to just get the portal running:

```env
TMDB_API_KEY=your-tmdb-api-key
ADMIN_USERNAMES=your-plex-username
PLEX_URL=http://192.168.1.50:32400
PLEX_TOKEN=your-plex-token
FULFILLMENT_API_KEY=change-me-to-a-long-random-secret
TZ=America/New_York
```

> The compose file always starts the `web` and `downloader` containers, but the downloader does
> nothing until you point it at a torrent client (see §6). It's harmless to leave running.

---

## 4. First run — become the admin

1. Open the portal and click **Sign in with Plex**. This runs Plex's PIN OAuth flow in a popup — you
   authorize with your own Plex account; PlexRequests never sees your Plex password.
   *(There's also an "Advanced: enter Plex token manually" option if the popup flow is inconvenient.)*
2. Because your Plex username is in `ADMIN_USERNAMES`, you land with the **Admin** role — you'll see the
   admin dashboard and the requests queue.
3. Have a friend sign in the same way; they get a normal user account and can start requesting.
4. Approve a request from the dashboard. Without a downloader configured, "approve" just marks it
   Approved for you to fulfill manually. With one (see §6), it's queued for automatic download.

---

## 5. Exposing it publicly

By default the portal is LAN-only. To let friends reach it over the internet, put it behind a tunnel
or reverse proxy that terminates TLS — **do not** expose port 8080 directly.

### Recommended: Cloudflare Tunnel

A Cloudflare Tunnel gives you an `https://requests.yourdomain.com` with a valid cert and no open
inbound ports on your router. The app is already configured to sit behind it correctly (it honors
`X-Forwarded-Proto`/`X-Forwarded-For`, so no redirect loops and secure auth cookies).

1. Add your domain to Cloudflare and install `cloudflared` on the host
   (<https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/>).
2. Create a tunnel and route a hostname to the container:

   ```yaml
   # ~/.cloudflared/config.yml
   tunnel: <your-tunnel-id>
   credentials-file: /root/.cloudflared/<your-tunnel-id>.json
   ingress:
     - hostname: requests.yourdomain.com
       service: http://localhost:8080
     - service: http_status:404
   ```
3. `cloudflared tunnel run <name>` (or install it as a service). Done — visit
   `https://requests.yourdomain.com`.

> **Why it "just works":** Cloudflare terminates HTTPS at the edge and forwards plain HTTP to the
> container. The app trusts the forwarded headers, so it sees the real `https` scheme — avoiding the
> classic `ERR_TOO_MANY_REDIRECTS` loop and ensuring the auth cookie gets its `Secure` flag and Plex
> OAuth callbacks are built as `https://`.

### Alternative: nginx (or any reverse proxy)

If you already run nginx with your own certs, proxy to the container and **forward the standard
headers** (the app relies on them):

```nginx
server {
    listen 443 ssl;
    server_name requests.yourdomain.com;

    ssl_certificate     /etc/letsencrypt/live/requests.yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/requests.yourdomain.com/privkey.pem;

    location / {
        proxy_pass         http://127.0.0.1:8080;
        proxy_http_version 1.1;

        # Required — the app reads these to know the real scheme/client
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;

        # Blazor Server uses WebSockets — these keep the SignalR circuit alive
        proxy_set_header   Upgrade           $http_upgrade;
        proxy_set_header   Connection        "upgrade";
        proxy_read_timeout 100s;
    }
}
```

### Other tunnels

Tailscale Funnel, ngrok, Traefik, Caddy, etc. all work — the only requirement is that they forward
`X-Forwarded-Proto` (and ideally `X-Forwarded-For`) and support WebSocket upgrades for the Blazor
circuit. Caddy and Traefik do this automatically.

---

## 6. Optional: automatic downloading

Approved requests can be fulfilled automatically by the out-of-process **downloader**: it searches
indexers (EZTV, YTS, 1337x, Nyaa, ext.to), ranks releases by quality/seeders, hands the best magnet to
a torrent client, hardlinks the finished files into your library, and calls back so the app marks the
request **Available** and reindexes Plex.

Set `FULFILLMENT_API_KEY` (the same value on web + downloader) and pick a scenario:

| Scenario | Command | What runs |
|----------|---------|-----------|
| **Bring your own client** | `docker compose up -d` | web + downloader. Set `DELUGE_URL` to your existing Deluge; `VPN_ENABLED=false`. |
| **Managed torrent client** | `docker compose --profile torrent up -d` | + a Deluge container the downloader auto-uses (`http://deluge:8112`). |
| **Managed VPN + client (kill-switch)** | `docker compose -f docker-compose.vpn.yml up -d` | web + gluetun + Deluge + downloader, with the torrent stack locked to the VPN tunnel. |

Full design, the worker API contract, indexer notes (including the Cloudflare caveat for 1337x/ext.to),
and VPN details are in **[docs/fulfillment-pipeline.md](docs/fulfillment-pipeline.md)**.

> **Legal note:** you are responsible for what you download and for complying with your local laws and
> the terms of any service you use. The indexer integrations are provided as-is.

---

## 7. Optional: Discord bridge

There's a companion **PlexBot** extension (`Extensions/PlexRequestsBridge/` in the PlexBot repo) that
lets users search and request from Discord, posts rich embeds with artwork for new requests, and lets
admins approve/deny with buttons. It talks to this app's `/api/bridge/*` endpoints.

To enable the server side here: set `BRIDGE_ENABLED=true` and a shared `BRIDGE_API_KEY`, then point the
extension at your portal URL with the same key. Users link their Discord to their portal account with a
code generated on their **Profile** page. See the extension's own README for the bot-side setup.

---

## 8. Configuration reference

All settings are read from `.env` (mapped to the app's config keys). Only the first group is required.

| `.env` variable | Required | Purpose |
|-----------------|:--------:|---------|
| `TMDB_API_KEY` | ✅ | TMDB v3 API key — metadata & artwork. |
| `ADMIN_USERNAMES` | ✅ | Comma-separated Plex usernames granted the Admin role on login. |
| `PLEX_URL` | ✅ | Your Plex server URL incl. port, e.g. `http://192.168.1.50:32400`. |
| `PLEX_TOKEN` | ✅ | Server-owner Plex token (library availability checks). |
| `PLEX_ALLOW_INVALID_CERTS` | | `true` if your Plex uses a self-signed HTTPS cert. |
| `TMDB_READ_ACCESS_TOKEN` | | Optional TMDB v4 read token (alternative to the v3 key). |
| `FULFILLMENT_API_KEY` | ▲ | Shared secret between web app and downloader. Required to use §6. |
| `FULFILLMENT_ENABLED` | | Queue jobs on approval (default on in the compose files). |
| `BRIDGE_ENABLED` / `BRIDGE_API_KEY` | ▲ | Enable + secure the Discord bridge API. Required for §7. |
| `DELUGE_URL` / `DELUGE_PASSWORD` | | Your torrent client's Web API (downloader). |
| `VPN_ENABLED` | | Only meaningful when the downloader runs inside a VPN namespace. |
| `VPN_PROVIDER`, `WIREGUARD_*`, `VPN_COUNTRIES`, `DOCKER_SUBNET` | | Managed-VPN stack only (`docker-compose.vpn.yml`). |
| `TZ` | | Container timezone, e.g. `America/New_York`. |
| `DB_PATH` | | Override the SQLite path (defaults to a Docker volume at `/data/app.db`). |

▲ = required only for the corresponding optional feature. See
[docker/.env.example](docker/.env.example) for a copy-paste template.

---

## 9. Backups & data

Everything the app owns lives in two Docker volumes:

- `appdata` → `/data/app.db` — the SQLite database (users, requests, watchlists).
- `./keys` → `/app/keys` — DataProtection keys. **Keep these** — if you lose them, existing login
  cookies are invalidated and everyone must sign in again.

To back up, stop the stack and copy the `app.db` file and the `keys/` directory. That's the whole state.

---

## 10. Running from source (development)

Requires the **.NET 10 SDK**.

```bash
# Web portal
dotnet run --project PlexRequestsHosted.csproj
# → http://localhost:5234  (https on :7231)

# Downloader worker (separate process)
dotnet run --project PlexRequests.Downloader
```

For local dev, put your keys in environment variables or `appsettings.Development.json` (which is
gitignored) instead of `.env`. The database migrates itself on startup (EF Core migrations).

---

## 11. Troubleshooting

### Downloads finish in the torrent client, but the request never turns Available

Symptoms: the torrent shows 100% / seeding in your client, but the request stays **Downloading** (or
flips to **Failed**), and the downloader logs mention *"could not resolve an on-disk path"* or a
file-not-found.

Almost always this is a **path mismatch**. For the downloader to import a finished download it must be
able to read the files at the **exact absolute path your torrent client reports for them**, and the
finished files and your library must live on **one filesystem** (so it can hardlink instead of copy).

- **The managed torrent modes already satisfy this** — with `docker compose --profile torrent` or the
  VPN stack, Deluge and the downloader share the same `media` volume mounted at `/data`, so the paths
  always line up. Nothing to configure.
- **Bring-your-own-client is where it bites** — most often a Deluge/qBittorrent installed **natively on
  the host** (or in a separate container with different mounts). Your client reports a *host* path such
  as `/srv/media/downloads/Movie/file.mkv`, but inside the downloader container that path doesn't exist
  (the container only has the shared volume at `/data`), so the import can't find the file. Waiting
  longer won't help: the built-in retry/grace window only covers the client still flushing to disk — it
  can't fix a path that will never resolve.

**Fix:** expose the host's download directory to the downloader container at the *identical* absolute
path, and put the library on that same filesystem. Create a `docker-compose.override.yml` next to the
compose file (Docker Compose merges it in automatically):

```yaml
# docker-compose.override.yml
services:
  downloader:
    volumes:
      # left  = the host directory your torrent client downloads into
      # right = the SAME absolute path, so the paths your client reports resolve inside the container
      - /srv/media:/srv/media
    environment:
      # keep the library on the same filesystem as the downloads → hardlinks work (no full copy)
      Library__MoviePath: /srv/media/library/movies
      Library__TvPath: /srv/media/library/tv
```

Then `docker compose up -d`. To confirm the mapping, ask the container to list a path your client
reports — it should show the file:

```bash
docker exec plexrequests-downloader ls -l /srv/media/downloads/<...>
```

> **Rule of thumb (same as Sonarr/Radarr):** every component that touches the files — the torrent
> client *and* the downloader — must reach them through one shared, identically-mapped path, with
> downloads and library on the same filesystem. Mismatched paths are the single most common cause of
> "downloaded but never imported."

If your torrent client runs on a **different machine**, the downloader can't hardlink or move the files
across the network on its own. Mount that machine's download share on the Docker host and pass it
through at matching paths, or add it under **Admin → Network Drives**.

### Managed Deluge connects, but downloads stall at 0% / "no peers"

The torrent client is reachable but its traffic isn't getting out — commonly because a VPN or firewall
already on the host is blocking or misrouting the container's connections. Two supported ways forward:

- Run the torrent stack behind the built-in kill-switch, so its only egress is the VPN tunnel:
  `docker compose -f docker-compose.vpn.yml up -d` (fill in the `VPN_PROVIDER` / `WIREGUARD_*` vars in
  `.env`).
- Or point `DELUGE_URL` at a torrent client you run on the host yourself (for a host-native client, use
  `http://host.docker.internal:8112`) — then apply the path-mapping fix above so imports can find the
  finished files.

### Other quick checks

- **Nothing queues on approval.** Set `FULFILLMENT_API_KEY` to the *same* value on both the web and
  downloader containers, and make sure `FULFILLMENT_ENABLED` isn't turned off.
- **`ERR_TOO_MANY_REDIRECTS` behind a proxy.** Your reverse proxy isn't forwarding `X-Forwarded-Proto`
  — see [§5](#5-exposing-it-publicly).
- **Everyone got logged out after a redeploy.** The `./keys` directory (DataProtection keys) wasn't
  persisted — see [§9](#9-backups--data).

---

*PlexRequests is not affiliated with Plex, Inc. or TMDB. "Plex" is a trademark of Plex, Inc.
This project is for use with content you are legally entitled to access.*
