#!/usr/bin/env bash
set -euo pipefail

repository_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
extension_dir="$repository_dir/BrowserExtension/firefox"
artifact_dir="$repository_dir/artifacts"
artifact_path="$artifact_dir/plexrequests-firefox-capture.zip"

mkdir -p "$artifact_dir"
artifact_dir="$(cd "$artifact_dir" && pwd)"
artifact_path="$artifact_dir/plexrequests-firefox-capture.zip"
if [[ "$artifact_dir" != "$repository_dir/artifacts" || "$artifact_path" != "$repository_dir/artifacts/plexrequests-firefox-capture.zip" ]]; then
  echo "Refusing to package outside the repository artifacts directory." >&2
  exit 1
fi
rm -f "$artifact_path"
(
  cd "$extension_dir"
  zip -q -r "$artifact_path" . -x 'tests/*' 'tests/**' '*.DS_Store'
)
unzip -tq "$artifact_path" >/dev/null
echo "$artifact_path"
