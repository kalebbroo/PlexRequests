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

  return { recordSource, queueSnapshot };
});
