(function exposeTelemetry(root, factory) {
  const sources = typeof module === "object" && module.exports
    ? require("./sources.js")
    : root.PlexRequestsCaptureSources;
  const telemetry = factory(sources);
  root.PlexRequestsCaptureTelemetry = telemetry;
  if (typeof module === "object" && module.exports) module.exports = telemetry;
})(typeof globalThis !== "undefined" ? globalThis : this, function createTelemetry(sources) {
  "use strict";

  function recordSource(record) {
    return record?.sourceKey || sources.fromUrl(record?.pageUrl || record?.sourceUrl)?.key || null;
  }

  function queueSnapshot(sourceKey, captures, details, state = {}, now = Date.now()) {
    const sourceCaptures = (captures || []).filter(record => recordSource(record) === sourceKey);
    const sourceDetails = (details || []).filter(record => recordSource(record) === sourceKey);
    const pauses = state.hydrationPauses || {};
    const sourcePause = Number(pauses[sourceKey]);
    const legacyPause = Object.keys(pauses).length ? 0 : Number(state.hydrationPausedUntil);
    const pausedUntil = sourcePause > now ? sourcePause : legacyPause > now ? legacyPause : null;
    const attention = record => record.state === "failed" || Boolean(record.needsAttention);

    return {
      captureEnabled: state.captureEnabled !== false,
      queuedUploads: sourceCaptures.filter(record => record.state === "queued").length,
      failedUploads: sourceCaptures.filter(record => record.state === "failed").length,
      pendingDetails: sourceDetails.filter(record =>
        (record.state === "queued" || record.state === "loading") && !attention(record)).length,
      attentionDetails: sourceDetails.filter(attention).length,
      hydrationPausedUntil: pausedUntil
    };
  }

  function isVersionOlder(installedVersion, currentVersion) {
    const parts = value => {
      const text = String(value || "").trim();
      if (!/^\d+(?:\.\d+){0,3}$/.test(text)) return null;
      return text.split(".").map(Number);
    };
    const installed = parts(installedVersion);
    const current = parts(currentVersion);
    if (!current) return false;
    if (!installed) return true;
    for (let index = 0; index < Math.max(installed.length, current.length); index++) {
      const difference = (installed[index] || 0) - (current[index] || 0);
      if (difference) return difference < 0;
    }
    return false;
  }

  function connectionChanges(serverStatus, installedVersion) {
    const changes = {};
    if (typeof serverStatus?.expiresAt === "string" && !Number.isNaN(Date.parse(serverStatus.expiresAt))) {
      changes.expiresAt = serverStatus.expiresAt;
    }
    if (typeof serverStatus?.currentExtensionVersion === "string" && serverStatus.currentExtensionVersion.trim()) {
      changes.currentExtensionVersion = serverStatus.currentExtensionVersion;
      changes.updateAvailable = isVersionOlder(installedVersion, serverStatus.currentExtensionVersion);
    }
    return changes;
  }

  return { recordSource, queueSnapshot, isVersionOlder, connectionChanges };
});
