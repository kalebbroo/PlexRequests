# Plex Requests Firefox capture

This extension observes supported 1337x and EXT.to pages after Firefox has rendered them and submits structured
release metadata to Plex Requests. It does not read, export, or replay browser cookies.

Browsing a search, category, or home listing automatically queues its visible releases. For 1337x, Firefox
reuses one inactive worker tab and waits 8-15 seconds between detail navigations. EXT.to uses the same normal
detail-page flow, then activates the site's own **View Hash** control and builds the magnet from the revealed
infohash. This keeps Cloudflare and CSRF handling inside the real page session instead of replaying private
site endpoints from the extension. The queue is
durable across restarts, bounded at 2,000 pending details, and retries transient failures with backoff. Each
indexer has its own source-scoped pairing, so both sites can remain connected at the same time. If a browser
challenge appears, the worker pauses; complete the challenge in a normal tab for that site and choose
**Resume detail capture** in the extension. The extension never clicks or solves browser challenges itself.
Once per minute it reports only source-scoped queue counts, capture/pause state, and its version so the admin
screen can show whether work is flowing or needs attention. It never includes release names, page URLs, or
browser session data in that health report. A valid device lease renews automatically near expiry while these
heartbeats continue. Inactive profiles still expire and administrators can revoke a device immediately.

Capture uploads keep 2,000 slots reserved for recoverable work. Permanently rejected pages are retained in a
separate diagnostic window capped at 100 records and seven days, so obsolete parser output cannot eventually
fill the durable queue and block every new page. A page refused during temporary queue backpressure is retried
while it remains open, and detail hydration is not completed until its upload is durably queued. Revisiting a
terminal batch explicitly revives it instead of mistaking the failed record for a successful duplicate.

When the paired server is distributing a newer extension, both the admin indexer health and the Firefox
popup show the manifest-derived version and link to the current XPI. Reloading the same extension ID keeps
its pairings and IndexedDB queues; the server never relies on a separately maintained version setting.

For both supported adapters, Firefox also reconciles the server's unresolved
catalog backlog every five minutes. This rebuilds missing local detail work after an extension update,
browser storage loss, or interrupted profile. Only public detail URLs are stored by Plex Requests; cookies,
CSRF tokens, and other browser session material remain inside Firefox and are never uploaded.

Hydration failures are never permanently abandoned. Interrupted work is recovered after Firefox restarts;
ordinary failures retry with capped exponential backoff, while repeated failures and expired site sessions
move to a slower “Needs attention” cadence. Revisiting a listing refreshes its session-bound data and moves it
back to the fast queue. Challenge pauses are scoped to one indexer, so EXT.to can keep resolving while 1337x
is waiting for verification, and vice versa.

The detail queue always reserves at least 1,600 of its 2,000 slots for fresh or actively recoverable work.
Needs-attention diagnostics are retained for up to 14 days, capped at 200 per source and 400 overall. Evicted
work for either adapter is rebuilt from the durable server backlog.

Firefox's install prompt explicitly discloses the required transmission of supported-site browsing activity, search
terms, and visible release metadata to the Plex Requests server you pair. No cookies or general browser
history are read or transmitted.

## Development installation

1. Open `about:debugging#/runtime/this-firefox`.
2. Download the extension XPI from **Admin → Acquisition → Indexers → Firefox capture**.
3. Choose **Load Temporary Add-on** and select the downloaded XPI.
4. Generate a pairing code and enter it in the extension popup with the Plex Requests URL. Repeat from the
   other indexer row to keep both sources paired.

Temporary extensions are removed when Firefox exits. A normal Firefox installation requires an XPI signed
by Mozilla. Use an unlisted AMO submission for private self-distribution; never commit AMO credentials.

## Packaging and checks

Run `./scripts/test-firefox-extension.sh` to execute parser tests, syntax checks, and manifest safety checks.
Run `./scripts/package-firefox-extension.sh` to create an unsigned development XPI under `artifacts/`.
