#!/usr/bin/env bash
set -euo pipefail

extension_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../BrowserExtension/firefox" && pwd)"
node --check "$extension_dir/background.js"
node --check "$extension_dir/capture-queue.js"
node --check "$extension_dir/content.js"
node --check "$extension_dir/hydration.js"
node --check "$extension_dir/parser.js"
node --check "$extension_dir/sources.js"
node --check "$extension_dir/telemetry.js"
node --check "$extension_dir/popup/popup.js"
node --test "$extension_dir/tests"/*.test.js

artifact_path="$("$extension_dir/../../scripts/package-firefox-extension.sh")"
archive_entries="$(unzip -Z1 "$artifact_path")"
for required_asset in \
  manifest.json background.js capture-queue.js content.js hydration.js parser.js sources.js telemetry.js \
  popup/popup.html popup/popup.css popup/popup.js icons/capture.svg
do
  if ! printf '%s\n' "$archive_entries" | grep -Fxq "$required_asset"; then
    echo "Packaged Firefox extension is missing $required_asset" >&2
    exit 1
  fi
done
if printf '%s\n' "$archive_entries" | grep -Eq '^tests/'; then
  echo "Packaged Firefox extension unexpectedly contains tests" >&2
  exit 1
fi
