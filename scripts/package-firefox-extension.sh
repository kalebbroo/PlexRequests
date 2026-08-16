#!/usr/bin/env bash
set -euo pipefail

repository_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
extension_dir="$repository_dir/BrowserExtension/firefox"
artifact_dir="$repository_dir/artifacts"
artifact_path="$artifact_dir/plexrequests-firefox-capture.xpi"

mkdir -p "$artifact_dir"
artifact_dir="$(cd "$artifact_dir" && pwd)"
artifact_path="$artifact_dir/plexrequests-firefox-capture.xpi"
if [[ "$artifact_dir" != "$repository_dir/artifacts" || "$artifact_path" != "$repository_dir/artifacts/plexrequests-firefox-capture.xpi" ]]; then
  echo "Refusing to package outside the repository artifacts directory." >&2
  exit 1
fi
rm -f "$artifact_path"
(
  cd "$extension_dir"
  zip -q -r "$artifact_path" manifest.json background.js content.js parser.js popup icons README.md \
    -x '*.DS_Store'
)
echo "$artifact_path"
