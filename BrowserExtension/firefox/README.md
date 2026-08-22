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
