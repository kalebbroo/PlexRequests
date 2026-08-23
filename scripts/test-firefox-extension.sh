#!/usr/bin/env bash
set -euo pipefail

extension_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../BrowserExtension/firefox" && pwd)"
node --check "$extension_dir/background.js"
node --check "$extension_dir/content.js"
node --check "$extension_dir/hydration.js"
node --check "$extension_dir/parser.js"
node --check "$extension_dir/sources.js"
node --check "$extension_dir/telemetry.js"
node --check "$extension_dir/popup/popup.js"
node --test "$extension_dir/tests"/*.test.js
