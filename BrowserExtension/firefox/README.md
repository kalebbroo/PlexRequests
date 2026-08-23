# Plex Requests Firefox capture

This extension observes supported 1337x and EXT.to pages after Firefox has rendered them and submits structured
release metadata to Plex Requests. It does not read, export, or replay browser cookies.

Browsing a search, category, or home listing automatically queues its visible releases. For 1337x, Firefox
reuses one inactive worker tab and waits 8-15 seconds between detail navigations. For EXT.to, it uses the
site's session-bound magnet request directly, so you do not need to open each torrent page. The queue is
durable across restarts, bounded at 2,000 pending details, and retries transient failures with backoff. Each
indexer has its own source-scoped pairing, so both sites can remain connected at the same time. If a browser
challenge appears, the worker pauses; complete the challenge in a normal tab for that site and choose
**Resume detail capture** in the extension. The extension never clicks or solves browser challenges itself.
Once per minute it reports only source-scoped queue counts, capture/pause state, and its version so the admin
screen can show whether work is flowing or needs attention. It never includes release names, page URLs, or
browser session data in that health report. A valid device lease renews automatically near expiry while these
heartbeats continue. Inactive profiles still expire and administrators can revoke a device immediately.

When the paired server is distributing a newer extension, both the admin indexer health and the Firefox
popup show the manifest-derived version and link to the current XPI. Reloading the same extension ID keeps
its pairings and IndexedDB queues; the server never relies on a separately maintained version setting.

For adapters whose detail URLs can be safely revisited, Firefox also reconciles the server's unresolved
catalog backlog every five minutes. This rebuilds missing local detail work after an extension update,
browser storage loss, or interrupted profile. The capability currently applies to 1337x. EXT.to remains
listing-driven because its magnet requests require short-lived credentials that are intentionally never
stored by Plex Requests.

Hydration failures are never permanently abandoned. Interrupted work is recovered after Firefox restarts;
ordinary failures retry with capped exponential backoff, while repeated failures and expired site sessions
move to a slower “Needs attention” cadence. Revisiting a listing refreshes its session-bound data and moves it
back to the fast queue. Challenge pauses are scoped to one indexer, so EXT.to can keep resolving while 1337x
is waiting for verification, and vice versa.

Firefox's install prompt explicitly discloses the required transmission of supported-site browsing activity, search
terms, and visible release metadata to the Plex Requests server you pair. No cookies or general browser
history are read or transmitted.

## Development installation

1. Open `about:debugging#/runtime/this-firefox`.
2. Choose **Load Temporary Add-on**.
3. Select this directory's `manifest.json`.
4. In Plex Requests, open **Admin → Acquisition → Indexers**, then open **Firefox capture** on 1337x or EXT.to.
5. Generate a pairing code and enter it in the extension popup with the Plex Requests URL. Repeat from the
   other indexer row to keep both sources paired.

Temporary extensions are removed when Firefox exits. A normal Firefox installation requires an XPI signed
by Mozilla. Use an unlisted AMO submission for private self-distribution; never commit AMO credentials.

## Packaging and checks

Run `./scripts/test-firefox-extension.sh` to execute parser tests, syntax checks, and manifest safety checks.
Run `./scripts/package-firefox-extension.sh` to create an unsigned development XPI under `artifacts/`.
