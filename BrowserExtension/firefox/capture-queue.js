(function exposeCaptureQueue(root, factory) {
  const captureQueue = factory();
  root.PlexRequestsCaptureQueue = captureQueue;
  if (typeof module === "object" && module.exports) module.exports = captureQueue;
})(typeof globalThis !== "undefined" ? globalThis : this, function createCaptureQueue() {
  "use strict";

  function failureTime(record) {
    const failedAt = Number(record?.failedAt);
    if (Number.isFinite(failedAt) && failedAt > 0) return failedAt;
    const capturedAt = Date.parse(record?.capturedAt || "");
    return Number.isFinite(capturedAt) ? capturedAt : 0;
  }

  function retentionPlan(records, options = {}) {
    const now = Number.isFinite(options.now) ? options.now : Date.now();
    const retentionMs = Math.max(0, Number(options.failureRetentionMs) || 0);
    const maximum = Math.max(0, Math.floor(Number(options.maxFailures) || 0));
    const failures = (records || [])
      .filter(record => record?.state === "failed")
      .map((record, order) => ({ record, order, failedAt: failureTime(record) }))
      .filter(item => item.failedAt >= now - retentionMs)
      .sort((left, right) => right.failedAt - left.failedAt || right.order - left.order)
      .slice(0, maximum);
    const retainedFailures = new Set(failures.map(item => item.record));
    const retained = (records || []).filter(record => record?.state !== "failed" || retainedFailures.has(record));
    const discarded = (records || []).filter(record => record?.state === "failed" && !retainedFailures.has(record));
    return { retained, discarded };
  }

  function pendingCount(records) {
    return (records || []).filter(record => record?.state !== "failed").length;
  }

  return { failureTime, retentionPlan, pendingCount };
});
