# Plex Requests Firefox capture

This extension observes supported 1337x pages after Firefox has rendered them and submits structured
release metadata to Plex Requests. It does not read, export, or replay browser cookies.

## Development installation

1. Open `about:debugging#/runtime/this-firefox`.
2. Choose **Load Temporary Add-on**.
3. Select this directory's `manifest.json`.
4. In Plex Requests, open **Admin → Acquisition → Indexers**, then open **Firefox capture** on 1337x.
5. Generate a pairing code and enter it in the extension popup with the Plex Requests URL.

Temporary extensions are removed when Firefox exits. A normal Firefox installation requires an XPI signed
by Mozilla. Use an unlisted AMO submission for private self-distribution; never commit AMO credentials.

## Packaging and checks

Run `./scripts/test-firefox-extension.sh` to execute parser tests, syntax checks, and manifest safety checks.
Run `./scripts/package-firefox-extension.sh` to create an unsigned development XPI under `artifacts/`.
