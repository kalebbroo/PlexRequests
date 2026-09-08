# Storage safety and cleanup

Plex Requests treats disk admission, media import, and source cleanup as separate boundaries. A failure in
one phase must not make a later phase guess.

## Admission before download

The downloader preflights the complete selected plan before enqueueing its first item. It uses selected
manifest file sizes when available, otherwise the indexer's declared size, and finally a conservative
unknown-size fallback (8 GiB for video, 2 GiB for music). It groups requirements by physical filesystem and
reserves:

- the payload on the download workspace;
- the configured temporary-work percentage for extraction, joining, remuxing, and atomic writes;
- the final payload on a distinct library filesystem; or a second payload on the same filesystem in Copy
  mode; and
- the minimum free-space floor that must still remain afterward.

Reservations for active jobs are written into `active-jobs.json`, so concurrent claims and worker restarts
remain conservative. A missing mount or insufficient capacity defers the job before backend enqueue; normal
job backoff retries it later.

The thresholds live in **Admin → Library → Change import and file handling → Storage safety**. The current
worker heartbeat is shown in **Admin → Overview → Downloads**. Storage notifications are an opt-in admin
event under **Profile → Notifications**.

## Durable post-import cleanup

An import is not the same as cleanup. Each transfer retains `CleanupCompletedAt`, its last attempt, and any
bounded error in the web database. Immediate cleanup reports that result, and a background pass retries every
imported transfer that is still incomplete after a crash, deploy, backend outage, or API failure.

Retry cleanup requires the exact protocol/backend id and at least one matching verified import-audit row.
The destination files are rechecked before the backend is asked to remove anything. If the backend no longer
contains the transfer, cleanup is complete because the intended state already exists. Missing audit or an
unverifiable destination always retains the source.

The admin's transfer policy still decides whether backend removal also removes payload data:

- **Move** removes source data after verified import.
- **Copy + Delete source after import** removes it.
- **Copy** without that option removes only backend ownership and retains its payload.
- **Hardlink** never deletes the shared source inode.

## Stale temporary artifacts

Every 15 minutes by default, the worker performs a bounded scan of configured storage roots. Automatic
cleanup is intentionally limited to names exclusively created by Plex Requests:

- inactive child folders beneath `.plexrequests-staging`; and
- hidden atomic files matching `.*.plexrequests-*.partial`.

Active job staging, recent artifacts inside the grace period, symbolic-link directories, ordinary partial
downloads, torrent payloads, and library media do not qualify. When automatic cleanup is disabled, the same
candidates are reported to the admin dashboard without being removed.

`STORAGE_STATUS_INTERVAL_SECONDS` controls the lightweight heartbeat. `STORAGE_ARTIFACT_SCAN_INTERVAL_MINUTES`
controls the bounded tree scan. Safety thresholds and cleanup behavior remain live admin settings.
