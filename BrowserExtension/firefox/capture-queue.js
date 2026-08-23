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

  function admissionPlan(records, batchId, maximum = 2000) {
    const all = records || [];
    const existing = all.find(record => record?.batchId === batchId) || null;
    if (existing) {
      return { disposition: existing.state === "failed" ? "revive" : "duplicate" };
    }
    const capacity = Math.max(0, Math.floor(Number(maximum) || 0));
    return {
      disposition: pendingCount(all) >= capacity ? "full" : "queue"
    };
  }

  function isDurablyQueued(result) {
    return Boolean(result?.queued || result?.duplicate);
  }

  return { failureTime, retentionPlan, pendingCount, admissionPlan, isDurablyQueued };
});
