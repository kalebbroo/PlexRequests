"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

test("manifest is Firefox MV3 and never requests cookie or browsing-history access", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "manifest.json"), "utf8"));
  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.version, "1.2.0");
  assert.equal(manifest.browser_specific_settings.gecko.strict_min_version, "140.0");
  assert.equal(manifest.browser_specific_settings.gecko_android.strict_min_version, "142.0");
  assert.deepEqual(
    manifest.browser_specific_settings.gecko.data_collection_permissions.required,
    ["browsingActivity", "websiteContent", "searchTerms"]
  );
  assert.ok(manifest.background.scripts.includes("hydration.js"));
  assert.ok(manifest.background.scripts.includes("sources.js"));
  assert.ok(manifest.background.scripts.includes("background.js"));
  assert.ok(manifest.permissions.includes("tabs"));
  assert.ok(!manifest.permissions.includes("cookies"));
  assert.ok(!manifest.permissions.includes("history"));
  assert.ok(!manifest.permissions.includes("webRequest"));
  const matches = manifest.content_scripts.flatMap(script => script.matches);
  assert.ok(matches.some(pattern => pattern.includes("1337x")));
  assert.ok(matches.some(pattern => pattern.includes("ext.to")));
});
