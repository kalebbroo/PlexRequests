# Fulfillment Pipeline

How an approved request becomes an "Available" title. The web app (this repo) owns the **request
lifecycle and the integration contract**; the actual downloading runs in a **separate, out-of-process
service** on a VPN-locked machine. Keeping the downloader out-of-process means this web app stays
deployable anywhere and the VPN/kill-switch failsafes live where they belong.

```
┌──────────────────────────── PlexRequests web app (this repo) ────────────────────────────┐
│  Admin approves ──► MediaRequestService.ApproveRequestAsync                                │
│                       └─ if Fulfillment:Enabled ─► IFulfillmentQueue.EnqueueAsync           │
│                                                        └─ FulfillmentJobs table (Queued)     │
│                                                                                             │
│  Secured worker API (X-Fulfillment-Key header, constant-time compare):                      │
│    POST /api/fulfillment/claim                 -> claim N queued jobs                        │
│    POST /api/fulfillment/{jobId}/progress      -> job Downloading, request Processing        │
│    POST /api/requests/{id}/fulfilled           -> request Available + Plex reindex + notify   │
│    POST /api/requests/{id}/failed              -> request Failed + notify admins              │
└───────────────────────────────────────────────▲───────────────────┬───────────────────────┘
                                                 │ claim / callbacks  │ jobs
                                                 │                    ▼
┌──────────────────────── Downloader service (separate repo, VPN box) ──────────────────────┐
│  poll/claim ─► search indexers ─► parse+rank quality ─► torrent client ─► move to library   │
│                                        (VPN kill-switch guards the torrent client)          │
│                                                        └─► callback fulfilled / failed        │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

## Status: what exists today

**In-app contract (built & verified in this repo):**
- `FulfillmentJobEntity` / `FulfillmentJobs` table (EF migration `AddFulfillmentJobs`).
- `IFulfillmentQueue` + DB-backed `FulfillmentQueue` (enqueue, claim, progress, complete, fail).
- Enqueue-on-approve, gated by `Fulfillment:Enabled`.
- The four worker endpoints above, gated by a shared secret (`Fulfillment:ApiKey`).
- Request status transitions Approved → Processing → Available (or → Failed), requester/admin
  notifications, and a best-effort Plex availability reindex on completion.

**Not built (this document is the design):** the downloader service itself. Nothing in this repo
touches torrents, indexers, or a VPN — "Available" is still a manual admin action unless a downloader
is deployed and pointed at these endpoints.

---

## The integration contract (authoritative reference)

All worker endpoints require the header `X-Fulfillment-Key: <secret>` matching `Fulfillment:ApiKey`
(compared in constant time). They are **not** cookie-authenticated. With no key configured, every
call is rejected — the pipeline is off by default.

### `POST /api/fulfillment/claim`
Claim queued jobs. Body: `{ "workerId": "vpnbox", "max": 10 }` (max clamped 1–25).
Returns an array of jobs; each claimed job flips `Queued → Claimed` and increments `Attempts`:
```json
[{ "id": 1, "mediaRequestId": 1, "mediaId": 550, "mediaType": 0,
   "title": "Fight Club", "tmdbId": 550, "imdbId": null, "tvdbId": null,
   "requestedSeasons": [], "quality": 0, "status": 1, "attempts": 1, "progress": 0 }]
```
`mediaType`: 0=Movie 1=TvShow 2=Music 3=Anime. `quality`: 0=Any 480/720/1080/2160/4320.

### `POST /api/fulfillment/{jobId}/progress`
Body: `{ "progress": 42, "workerId": "vpnbox" }`. Sets job `Downloading` + percentage; flips the
linked request `Approved → Processing` (so the UI can show "Downloading… 42%").

### `POST /api/requests/{id}/fulfilled`
No body. Idempotent. Marks the request `Available` (+`AvailableAt`), closes the job `Completed`,
triggers `RebuildAvailabilityIndexAsync` (best-effort), and notifies the requester. Re-calling on an
already-available request is a no-op 200.

### `POST /api/requests/{id}/failed`
Body: `{ "reason": "no seeders found" }`. Marks the request `Failed`, closes the job `Failed`
(stores the reason), and notifies **admins** (needs-attention). The request can be re-approved to
re-enqueue.

**Idempotency:** callbacks are safe to retry — the app no-ops when the target is already in the
terminal state. The worker should retry callbacks until it gets a 2xx.

---

## Downloader service design (4.2)

A standalone worker (its own repo/container) on the VPN-locked box. Suggested as a .NET worker or
any language that can speak HTTP + the torrent-client API. Single responsibility: turn a
`FulfillmentJob` into files in the Plex library, then call back.

### Stage 1 — Poll / claim
Long-poll `POST /api/fulfillment/claim` on an interval (e.g. every 10–30s), claiming up to N jobs
sized to the box's concurrency. Persist claimed jobs locally so a worker restart resumes them
(the app already recorded them as `Claimed`).

### Stage 2 — Search indexers
Release candidates come from a pluggable set of `IIndexerProvider`s, merged by `IndexerClient`. Built in:
- **EZTV** (`EztvIndexerProvider`) — TV, public JSON API keyed by IMDb id.
- **YTS** (`YtsIndexerProvider`) — movies, public JSON API keyed by IMDb id; magnet built from the hash.
- **1337x** (`X1337xIndexerProvider`) — movies + TV, **HTML scrape** (no API): the category-search page
  lists rows, and each torrent's magnet is fetched from its detail page (top N rows only, `X1337xMaxDetail`).
- **Nyaa** (`NyaaIndexerProvider`) — anime, via the **RSS feed** (no scraping): parses
  `nyaa:infoHash`/`nyaa:seeders`/`nyaa:size` and builds a magnet from the hash. Runs for Anime/TV/Movie
  (TMDB has no anime type), returning nothing for non-anime titles.
- **ext.to** (`ExtToIndexerProvider`) — movies + TV, **HTML scrape**: follows the top search results to
  their detail pages and extracts magnet + labelled Seeders/Size (falls back to inline magnets on the
  search page). Search path is configurable (`ExtToSearchPath`, `{query}` substituted) for tuning.

  ⚠️ **Cloudflare (1337x & ext.to):** both are usually behind Cloudflare, which blocks plain HTTP
  clients from datacenter IPs (each provider detects the challenge page and returns nothing, logging a
  warning). They generally work from a residential/VPN egress; if your exit IP is blocked, front them
  with a solver proxy (e.g. FlareSolverr) and point `X1337xBaseUrl`/`ExtToBaseUrl` at it, or disable
  them (`X1337xEnabled`/`ExtToEnabled: false`). EZTV/YTS/Nyaa use APIs/RSS and aren't affected.

To add more trackers, drop in another `IIndexerProvider` (or front a **Prowlarr/Jackett** aggregator).
- Movies: one query for the film (title + year).
- TV: per-season/episode driven by `requestedSeasons` (empty ⇒ all seasons).

### Stage 3 — Parse & rank quality
Parse release names into structured metadata and score them; reject anything below the requested
`quality` floor.
- Parse: resolution (480/720/1080/2160), source (BluRay/WEB-DL/WEBRip/HDTV), codec (x264/x265/AV1),
  HDR/DV, audio, release group, proper/repack.
- Signals: seeders/leechers, size sanity (min/max per runtime), trusted groups, freeleech.
- Score = weighted sum; pick the top candidate at or above `quality`. If `quality == Any`, prefer the
  best value (seeders × quality / size). No acceptable candidate ⇒ callback `failed` with a clear
  reason (so it surfaces to admins, not a silent stall).

### Stage 4 — Hand to the torrent client
Add the chosen magnet/torrent to **qBittorrent** or **Deluge** via its Web API, tagged with a
category that routes the completed files to the correct library path (movies vs TV). Record the
client's torrent hash against the job for progress polling and cleanup.

### Stage 5 — VPN failsafe (the reason this is out-of-process)
The torrent client must only ever talk through the VPN. Enforce **in depth**:
- Bind the torrent client to the VPN interface (e.g. `wg0`/`tun0`) — not the default route.
- A **kill-switch**: firewall rules (or the client's network-interface binding) that drop all
  torrent traffic if the VPN interface goes down.
- A health check in the worker: before adding a torrent and periodically during, verify the VPN is
  up and the public IP is the VPN's. On failure: pause all torrents, stop claiming, alert. Never
  fall back to the default route.

### Stage 6 — Completion → library → callback
On client "completed" (webhook or poll):
1. Move/hardlink files into the Plex library path for that category (hardlink to keep seeding).
2. Optionally verify the import.
3. Call `POST /api/requests/{id}/fulfilled`. The app reindexes Plex and notifies the requester.
On unrecoverable failure (no candidate, repeated errors, import failure): call
`POST /api/requests/{id}/failed` with the reason. Retry callbacks until 2xx.

---

## Deployment — modular VPN & torrent client

The VPN and the torrent client are **both optional** — bring your own, let the app manage them, or mix.
The code toggles are `Vpn:Enabled` and `Deluge:Url`; the compose files wire them per scenario:

| Scenario | Command | What runs |
|----------|---------|-----------|
| **BYO VPN + BYO client** | `docker compose up -d` | web + downloader. Set `DELUGE_URL` to your client; `VPN_ENABLED=false`. |
| **Managed client, no managed VPN** | `docker compose --profile torrent up -d` | + a Deluge container (downloader auto-uses `http://deluge:8112`). |
| **Managed VPN + client (kill-switch)** | `docker compose -f docker-compose.vpn.yml up -d` | web + gluetun + Deluge + downloader, with Deluge and the downloader in gluetun's netns. |

Notes:
- The app-level `IVpnGuard` only blocks work when the downloader is *inside* the VPN namespace and the
  tunnel drops (egress fails). On a normally-connected box it never blocks, so leaving it on is harmless.
- "Managed VPN + BYO client" isn't a sensible combo — the managed gluetun only protects containers routed
  through it, so it comes paired with the managed Deluge.
- Deluge (or any client) just needs to be reachable at `Deluge:Url` and expose its Web API.

## Security & ops (4.3)

- **Auth:** rotating shared secret in `X-Fulfillment-Key`, HTTPS only, constant-time compare
  (implemented). Consider IP-allowlisting the worker and rate-limiting the endpoints.
- **Credential isolation:** the downloader holds Plex, indexer, and torrent-client credentials; the
  web app holds none of them. Compromise of the web host doesn't expose the tracker/VPN stack.
- **Idempotency & retries:** all callbacks are idempotent; the worker retries with backoff. A claimed
  job that never completes should be re-queueable (see below).
- **Reindex cost:** `/fulfilled` currently reindexes per call. For bursts, debounce/coalesce reindex
  triggers or batch completions.

## Status surfacing (4.4)

`RequestStatus` already includes `Processing` (used for "Downloading") and `Failed`. The progress
endpoint moves a request to `Processing`; completion → `Available`; failure → `Failed`. The admin
requests page shows these states; a failed request is re-approvable to re-enqueue.

## Recommended follow-ups (not yet built)

- **Stale-claim reaper:** a background sweep that returns `Claimed`/`Downloading` jobs with no
  progress past a timeout back to `Queued` (or `Failed` after M attempts), so a dead worker doesn't
  strand requests.
- **Admin job view:** a page listing `FulfillmentJobs` with status/progress/attempts and a manual
  requeue/cancel action.
- **Per-request quality:** persist the requester's preferred `Quality` on the request so
  `EnqueueAsync` sends it (currently defaults to `Any`).

## Configuration reference

| Env var (.env) | Config key | Purpose |
|----------------|------------|---------|
| `FULFILLMENT_ENABLED` | `Fulfillment:Enabled` | Enqueue jobs on approve. Off ⇒ approvals are manual-only. |
| `FULFILLMENT_API_KEY` | `Fulfillment:ApiKey` | Shared secret for the worker API. Unset ⇒ endpoints reject all calls. |

Both are mapped from `.env` by `LoadDotEnvFrom` in `Program.cs`.
