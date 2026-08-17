"use strict";

const hydration = globalThis.PlexRequestsHydration;
const DATABASE_NAME = "plexrequests-browser-capture";
const DATABASE_VERSION = 2;
const CAPTURE_STORE_NAME = "captureQueue";
const HYDRATION_STORE_NAME = "hydrationQueue";
const DRAIN_ALARM = "drain-capture-queue";
const HYDRATION_ALARM = "drain-hydration-queue";
const MAX_QUEUE_ITEMS = 2000;
const MAX_HYDRATION_ITEMS = 2000;
const MAX_HYDRATION_ATTEMPTS = 4;
const HYDRATION_TIMEOUT_MS = 90_000;
const HYDRATION_CHALLENGE_PAUSE_MS = 15 * 60 * 1000;
const COMPLETED_HYDRATION_RETENTION_MS = 7 * 24 * 60 * 60 * 1000;
let drainPromise = null;
let hydrationPromise = null;
let hydrationTimer = null;

function openDatabase() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DATABASE_NAME, DATABASE_VERSION);
    request.onupgradeneeded = () => {
      const database = request.result;
      if (!database.objectStoreNames.contains(CAPTURE_STORE_NAME)) {
        const captureStore = database.createObjectStore(CAPTURE_STORE_NAME, { keyPath: "batchId" });
        captureStore.createIndex("nextAttemptAt", "nextAttemptAt");
      }
      if (!database.objectStoreNames.contains(HYDRATION_STORE_NAME)) {
        const hydrationStore = database.createObjectStore(HYDRATION_STORE_NAME, { keyPath: "externalId" });
        hydrationStore.createIndex("nextAttemptAt", "nextAttemptAt");
        hydrationStore.createIndex("state", "state");
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function withStore(storeName, mode, action) {
  const database = await openDatabase();
  try {
    return await new Promise((resolve, reject) => {
      const transaction = database.transaction(storeName, mode);
      const store = transaction.objectStore(storeName);
      let result;
      try { result = action(store); } catch (error) { reject(error); return; }
      transaction.oncomplete = () => resolve(result);
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error);
    });
  } finally {
    database.close();
  }
}

function requestResult(request) {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function allRecords(storeName) {
  return withStore(storeName, "readonly", store => requestResult(store.getAll()));
}

async function deleteRecord(storeName, key) {
  await withStore(storeName, "readwrite", store => store.delete(key));
}

async function updateRecord(storeName, record) {
  await withStore(storeName, "readwrite", store => store.put(record));
}

async function queueRecord(batch) {
  const current = await browser.storage.local.get({ captureEnabled: true });
  if (!current.captureEnabled) return { queued: false };

  if (batch.pageType === "listing") await enqueueHydrations(batch.items);
  const records = await allRecords(CAPTURE_STORE_NAME);
  if (records.some(item => item.batchId === batch.batchId)) {
    void drainHydrationQueue();
    return { queued: false, duplicate: true };
  }
  if (records.length >= MAX_QUEUE_ITEMS) {
    await browser.storage.local.set({
      lastError: `Capture queue is full (${MAX_QUEUE_ITEMS} pages). Pair or retry before browsing more pages.`
    });
    return { queued: false, full: true };
  }
  await updateRecord(CAPTURE_STORE_NAME, {
    ...batch,
    attempts: 0,
    nextAttemptAt: 0,
    state: "queued",
    lastError: null
  });
  await updateBadge();
  void drainQueue();
  void drainHydrationQueue();
  return { queued: true };
}

async function enqueueHydrations(items) {
  const now = Date.now();
  const records = await allRecords(HYDRATION_STORE_NAME);
  const retained = records.filter(item => item.state !== "complete" || (item.completedAt || 0) >= now - COMPLETED_HYDRATION_RETENTION_MS);
  const retainedIds = new Set(retained.map(item => item.externalId));
  const pendingCount = retained.filter(item => item.state !== "complete").length;
  const available = Math.max(0, MAX_HYDRATION_ITEMS - pendingCount);
  const candidateMap = new Map();
  for (const item of items || []) {
    const candidate = hydration.detailCandidate(item, now);
    if (candidate && !retainedIds.has(candidate.externalId)) candidateMap.set(candidate.externalId, candidate);
  }
  const candidates = [...candidateMap.values()].slice(0, available);

  await withStore(HYDRATION_STORE_NAME, "readwrite", store => {
    for (const record of records) {
      if (!retainedIds.has(record.externalId)) store.delete(record.externalId);
    }
    for (const candidate of candidates) store.put(candidate);
  });

  if (candidateMap.size > candidates.length) {
    await browser.storage.local.set({
      hydrationLastError: `Detail queue is full (${MAX_HYDRATION_ITEMS} releases). Let Firefox catch up before browsing more listings.`
    });
  }
  await updateBadge();
  return candidates.length;
}

async function queueStatus() {
  const [captures, details, state] = await Promise.all([
    allRecords(CAPTURE_STORE_NAME),
    allRecords(HYDRATION_STORE_NAME),
    browser.storage.local.get({ hydrationPausedUntil: null })
  ]);
  return {
    queued: captures.filter(item => item.state === "queued").length,
    failed: captures.filter(item => item.state === "failed").length,
    hydrationQueued: details.filter(item => item.state === "queued" || item.state === "loading").length,
    hydrationFailed: details.filter(item => item.state === "failed").length,
    hydrationPausedUntil: state.hydrationPausedUntil
  };
}

function normalizedServerUrl(value) {
  const url = new URL(String(value || "").trim());
  if (url.protocol !== "https:" && !(url.protocol === "http:" && ["localhost", "127.0.0.1"].includes(url.hostname))) {
    throw new Error("Use HTTPS, except for a local development server.");
  }
  return url.origin;
}

async function apiFetch(path, options = {}) {
  const config = await browser.storage.local.get(["serverUrl", "token"]);
  if (!config.serverUrl) throw new Error("Pair this extension with Plex Requests first.");
  const headers = new Headers(options.headers || {});
  headers.set("Accept", "application/json");
  if (options.body) headers.set("Content-Type", "application/json");
  if (config.token) headers.set("Authorization", `Bearer ${config.token}`);
  return fetch(`${config.serverUrl}${path}`, { ...options, headers });
}

async function pair(serverUrl, pairingCode, deviceName) {
  const server = normalizedServerUrl(serverUrl);
  const originPermission = `${server}/*`;
  const granted = await browser.permissions.contains({ origins: [originPermission] });
  if (!granted) throw new Error("Firefox needs permission to connect to this Plex Requests server.");

  await browser.storage.local.set({ serverUrl: server });
  const response = await apiFetch("/api/browser-capture/pair", {
    method: "POST",
    body: JSON.stringify({
      pairingCode,
      deviceName: String(deviceName || "Firefox").trim() || "Firefox",
      extensionVersion: browser.runtime.getManifest().version
    })
  });
  if (!response.ok) {
    const detail = await safeError(response);
    throw new Error(response.status === 401 ? "The pairing code is invalid, expired, or already used." : detail);
  }
  const paired = await response.json();
  await browser.storage.local.set({
    token: paired.token,
    tokenExpiresAt: paired.expiresAt,
    source: paired.source,
    lastError: null,
    hydrationLastError: null,
    connected: true
  });
  await drainQueue(true);
  void drainHydrationQueue(true);
  return paired;
}

async function connectionStatus(checkServer = false) {
  const config = await browser.storage.local.get({
    captureEnabled: true,
    connected: false,
    serverUrl: "",
    tokenExpiresAt: null,
    source: "",
    lastError: null,
    lastSuccessAt: null,
    acceptedItems: 0,
    hydratedItems: 0,
    hydrationLastError: null,
    hydrationPausedUntil: null
  });
  if (checkServer && config.serverUrl) {
    try {
      const response = await apiFetch("/api/browser-capture/status");
      config.connected = response.ok;
      if (!response.ok && response.status === 401) config.lastError = "Pairing expired or was revoked.";
      await browser.storage.local.set({ connected: config.connected, lastError: config.lastError });
    } catch (error) {
      config.connected = false;
      config.lastError = error.message;
    }
  }
  return { ...config, ...(await queueStatus()) };
}

async function safeError(response) {
  try {
    const body = await response.json();
    return body.error || `Server returned HTTP ${response.status}.`;
  } catch {
    return `Server returned HTTP ${response.status}.`;
  }
}

function retryDelay(attempts) {
  const base = Math.min(60 * 60 * 1000, 5000 * (2 ** Math.min(attempts, 9)));
  return base + Math.floor(Math.random() * Math.min(base / 4, 30_000));
}

async function sendRecord(record) {
  try {
    const response = await apiFetch("/api/browser-capture/batches", {
      method: "POST",
      body: JSON.stringify({
        batchId: record.batchId,
        pageUrl: record.pageUrl,
        pageType: record.pageType,
        parserVersion: record.parserVersion,
        extensionVersion: browser.runtime.getManifest().version,
        capturedAt: record.capturedAt,
        items: record.items
      })
    });
    if (response.ok) {
      const result = await response.json();
      await deleteRecord(CAPTURE_STORE_NAME, record.batchId);
      const state = await browser.storage.local.get({ acceptedItems: 0 });
      await browser.storage.local.set({
        connected: true,
        lastError: null,
        lastSuccessAt: new Date().toISOString(),
        acceptedItems: state.acceptedItems + (result.duplicateBatch ? 0 : result.acceptedItems)
      });
      return;
    }

    const detail = await safeError(response);
    record.attempts += 1;
    record.lastError = detail;
    if (response.status === 400) {
      record.state = "failed";
      record.nextAttemptAt = Number.MAX_SAFE_INTEGER;
    } else if (response.status === 401 || response.status === 403) {
      record.nextAttemptAt = Date.now() + 60 * 60 * 1000;
      await browser.storage.local.set({ connected: false, lastError: "Pairing expired or was revoked." });
    } else if (response.status === 409) {
      record.nextAttemptAt = Date.now() + 15 * 60 * 1000;
      await browser.storage.local.set({ lastError: detail });
    } else {
      const retryAfter = Number.parseInt(response.headers.get("Retry-After") || "", 10);
      record.nextAttemptAt = Date.now() + (Number.isFinite(retryAfter) ? retryAfter * 1000 : retryDelay(record.attempts));
      await browser.storage.local.set({ lastError: detail });
    }
    await updateRecord(CAPTURE_STORE_NAME, record);
  } catch (error) {
    record.attempts += 1;
    record.lastError = error.message;
    record.nextAttemptAt = Date.now() + retryDelay(record.attempts);
    await updateRecord(CAPTURE_STORE_NAME, record);
    await browser.storage.local.set({ lastError: error.message });
  }
}

async function drainQueue(force = false) {
  if (drainPromise) return drainPromise;
  drainPromise = (async () => {
    const config = await browser.storage.local.get({ captureEnabled: true, token: null });
    if (!config.captureEnabled || !config.token) return;
    const due = (await allRecords(CAPTURE_STORE_NAME))
      .filter(item => item.state === "queued" && (force || item.nextAttemptAt <= Date.now()))
      .sort((a, b) => a.nextAttemptAt - b.nextAttemptAt)
      .slice(0, 20);
    for (const record of due) await sendRecord(record);
  })().finally(async () => {
    drainPromise = null;
    await updateBadge();
  });
  return drainPromise;
}

async function retryFailedCaptures() {
  const records = await allRecords(CAPTURE_STORE_NAME);
  for (const record of records.filter(item => item.state === "failed")) {
    record.state = "queued";
    record.attempts = 0;
    record.nextAttemptAt = 0;
    record.lastError = null;
    await updateRecord(CAPTURE_STORE_NAME, record);
  }
  return drainQueue(true);
}

async function completeHydrations(items) {
  const externalIds = new Set((items || []).map(item => item.externalId).filter(Boolean));
  if (!externalIds.size) return 0;
  const records = await allRecords(HYDRATION_STORE_NAME);
  let completed = 0;
  for (const record of records.filter(item => externalIds.has(item.externalId) && item.state !== "complete")) {
    record.state = "complete";
    record.completedAt = Date.now();
    record.startedAt = null;
    record.lastError = null;
    await updateRecord(HYDRATION_STORE_NAME, record);
    completed++;
  }
  if (completed) {
    const state = await browser.storage.local.get({ hydratedItems: 0 });
    await browser.storage.local.set({
      hydratedItems: state.hydratedItems + completed,
      hydrationLastError: null
    });
  }
  return completed;
}

async function workerState() {
  return browser.storage.local.get({
    hydrationWorkerTabId: null,
    hydrationWorkerExternalId: null,
    hydrationWorkerStartedAt: null
  });
}

async function existingTab(tabId) {
  if (!Number.isInteger(tabId)) return null;
  try { return await browser.tabs.get(tabId); } catch { return null; }
}

async function clearWorker(closeTab = false) {
  const state = await workerState();
  await browser.storage.local.remove([
    "hydrationWorkerTabId", "hydrationWorkerExternalId", "hydrationWorkerStartedAt"
  ]);
  if (closeTab && await existingTab(state.hydrationWorkerTabId)) {
    try { await browser.tabs.remove(state.hydrationWorkerTabId); } catch { /* already closed */ }
  }
  return state;
}

async function retryHydration(externalId, error, delay = null) {
  if (!externalId) return;
  const records = await allRecords(HYDRATION_STORE_NAME);
  const record = records.find(item => item.externalId === externalId);
  if (!record || record.state === "complete") return;
  record.startedAt = null;
  record.lastError = error;
  if (record.attempts >= MAX_HYDRATION_ATTEMPTS) {
    record.state = "failed";
    record.nextAttemptAt = Number.MAX_SAFE_INTEGER;
  } else {
    record.state = "queued";
    record.nextAttemptAt = Date.now() + (delay ?? hydration.retryDelay(record.attempts));
  }
  await updateRecord(HYDRATION_STORE_NAME, record);
  await browser.storage.local.set({ hydrationLastError: error });
}

function scheduleHydration(delay = hydration.navigationDelay()) {
  clearTimeout(hydrationTimer);
  hydrationTimer = setTimeout(() => void drainHydrationQueue(), delay);
}

async function finishWorker(tabId, items) {
  const state = await workerState();
  await completeHydrations(items);
  if (state.hydrationWorkerTabId !== tabId) return;
  await browser.storage.local.set({
    hydrationWorkerExternalId: null,
    hydrationWorkerStartedAt: null,
    hydrationLastError: null
  });
  await updateBadge();
  scheduleHydration();
}

async function failWorker(tabId, reason) {
  const state = await workerState();
  if (state.hydrationWorkerTabId !== tabId) return;
  await retryHydration(state.hydrationWorkerExternalId, reason);
  await browser.storage.local.set({
    hydrationWorkerExternalId: null,
    hydrationWorkerStartedAt: null
  });
  await updateBadge();
  scheduleHydration();
}

async function pauseForChallenge(tabId) {
  const state = await workerState();
  if (state.hydrationWorkerTabId !== tabId) return;
  const pausedUntil = Date.now() + HYDRATION_CHALLENGE_PAUSE_MS;
  await retryHydration(
    state.hydrationWorkerExternalId,
    "1337x presented a browser challenge. Automatic detail capture is paused; open 1337x normally, complete it, then resume.",
    HYDRATION_CHALLENGE_PAUSE_MS
  );
  await browser.storage.local.set({
    hydrationPausedUntil: pausedUntil,
    hydrationLastError: "1337x challenge detected. Complete it in a normal tab, then resume detail capture."
  });
  await clearWorker(true);
  await updateBadge();
}

async function recoverStaleWorker() {
  const state = await workerState();
  if (!state.hydrationWorkerExternalId) return state;
  const tab = await existingTab(state.hydrationWorkerTabId);
  const stale = !state.hydrationWorkerStartedAt
    || state.hydrationWorkerStartedAt < Date.now() - HYDRATION_TIMEOUT_MS;
  if (tab && !stale) return state;
  await retryHydration(state.hydrationWorkerExternalId, tab ? "Detail page timed out." : "Detail worker tab was closed.");
  await clearWorker(Boolean(tab));
  return workerState();
}

async function drainHydrationQueue(force = false) {
  if (hydrationPromise) return hydrationPromise;
  hydrationPromise = (async () => {
    const config = await browser.storage.local.get({
      captureEnabled: true,
      token: null,
      hydrationPausedUntil: null
    });
    if (!config.captureEnabled || !config.token) return;
    if (!force && config.hydrationPausedUntil && config.hydrationPausedUntil > Date.now()) return;
    if (force && config.hydrationPausedUntil) {
      await browser.storage.local.set({ hydrationPausedUntil: null, hydrationLastError: null });
    }

    const current = await recoverStaleWorker();
    if (current.hydrationWorkerExternalId) return;
    const records = await allRecords(HYDRATION_STORE_NAME);
    const next = hydration.nextDue(records, Date.now());
    if (!next) {
      if (await existingTab(current.hydrationWorkerTabId)) await clearWorker(true);
      return;
    }

    next.state = "loading";
    next.attempts += 1;
    next.startedAt = Date.now();
    next.lastError = null;
    await updateRecord(HYDRATION_STORE_NAME, next);

    try {
      let tab = await existingTab(current.hydrationWorkerTabId);
      if (!tab) tab = await browser.tabs.create({ url: "about:blank", active: false });
      await browser.storage.local.set({
        hydrationWorkerTabId: tab.id,
        hydrationWorkerExternalId: next.externalId,
        hydrationWorkerStartedAt: next.startedAt
      });
      await browser.tabs.update(tab.id, { url: next.sourceUrl, active: false });
    } catch (error) {
      await retryHydration(next.externalId, error.message);
      await clearWorker(true);
      scheduleHydration();
    }
  })().finally(async () => {
    hydrationPromise = null;
    await updateBadge();
  });
  return hydrationPromise;
}

async function retryHydrations() {
  const records = await allRecords(HYDRATION_STORE_NAME);
  for (const record of records.filter(item => item.state === "failed" || item.state === "queued")) {
    record.state = "queued";
    record.attempts = 0;
    record.startedAt = null;
    record.nextAttemptAt = 0;
    record.lastError = null;
    await updateRecord(HYDRATION_STORE_NAME, record);
  }
  await browser.storage.local.set({ hydrationPausedUntil: null, hydrationLastError: null });
  return drainHydrationQueue(true);
}

async function retryAll() {
  await retryFailedCaptures();
  await retryHydrations();
  return connectionStatus(false);
}

async function setCaptureEnabled(enabled) {
  await browser.storage.local.set({ captureEnabled: Boolean(enabled) });
  if (!enabled) {
    const state = await workerState();
    await retryHydration(state.hydrationWorkerExternalId, "Detail capture paused.", 0);
    await clearWorker(true);
  } else {
    void drainQueue(true);
    void drainHydrationQueue(true);
  }
  return connectionStatus(false);
}

async function updateBadge() {
  const status = await queueStatus();
  const count = status.queued + status.failed + status.hydrationQueued + status.hydrationFailed;
  const paused = status.hydrationPausedUntil && status.hydrationPausedUntil > Date.now();
  await browser.action.setBadgeText({ text: count ? String(Math.min(count, 999)) : "" });
  await browser.action.setBadgeBackgroundColor({
    color: status.failed || status.hydrationFailed || paused ? "#d32f2f" : "#f59e0b"
  });
}

browser.runtime.onMessage.addListener((message, sender) => {
  switch (message && message.type) {
    case "queue-capture":
      return queueRecord(message.batch).then(async result => {
        if (message.batch.pageType === "detail") await finishWorker(sender.tab && sender.tab.id, message.batch.items);
        return result;
      });
    case "page-observation":
      if (message.pageType === "challenge") return pauseForChallenge(sender.tab && sender.tab.id);
      if (message.pageType === "unsupported-detail") {
        return failWorker(sender.tab && sender.tab.id, "The detail page loaded without a usable magnet link.");
      }
      return undefined;
    case "pair": return pair(message.serverUrl, message.pairingCode, message.deviceName);
    case "status": return connectionStatus(Boolean(message.checkServer));
    case "retry": return retryAll();
    case "resume-hydration": return retryHydrations().then(() => connectionStatus(false));
    case "set-enabled": return setCaptureEnabled(message.enabled);
    case "forget-pairing":
      return clearWorker(true).then(() => browser.storage.local.remove([
        "token", "tokenExpiresAt", "source", "connected", "lastError",
        "hydrationPausedUntil", "hydrationLastError"
      ]));
    default: return undefined;
  }
});

browser.tabs.onRemoved.addListener(tabId => {
  void workerState().then(async state => {
    if (state.hydrationWorkerTabId !== tabId) return;
    await retryHydration(state.hydrationWorkerExternalId, "Detail worker tab was closed.");
    await clearWorker(false);
    scheduleHydration();
  });
});

browser.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === DRAIN_ALARM) void drainQueue();
  if (alarm.name === HYDRATION_ALARM) void drainHydrationQueue();
});

function createAlarms() {
  browser.alarms.create(DRAIN_ALARM, { periodInMinutes: 1 });
  browser.alarms.create(HYDRATION_ALARM, { periodInMinutes: 1 });
}

browser.runtime.onInstalled.addListener(() => {
  createAlarms();
  void updateBadge();
  void drainHydrationQueue();
});
browser.runtime.onStartup.addListener(() => {
  createAlarms();
  void drainQueue();
  void drainHydrationQueue();
});
createAlarms();
void updateBadge();
void drainHydrationQueue();
