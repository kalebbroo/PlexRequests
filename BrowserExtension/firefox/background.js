"use strict";

const DATABASE_NAME = "plexrequests-browser-capture";
const STORE_NAME = "captureQueue";
const DRAIN_ALARM = "drain-capture-queue";
const MAX_QUEUE_ITEMS = 2000;
let drainPromise = null;

function openDatabase() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DATABASE_NAME, 1);
    request.onupgradeneeded = () => {
      const store = request.result.createObjectStore(STORE_NAME, { keyPath: "batchId" });
      store.createIndex("nextAttemptAt", "nextAttemptAt");
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function withStore(mode, action) {
  const database = await openDatabase();
  try {
    return await new Promise((resolve, reject) => {
      const transaction = database.transaction(STORE_NAME, mode);
      const store = transaction.objectStore(STORE_NAME);
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

async function queueRecord(batch) {
  const current = await browser.storage.local.get({ captureEnabled: true });
  if (!current.captureEnabled) return { queued: false };
  const records = await allRecords();
  if (records.some(item => item.batchId === batch.batchId)) return { queued: false, duplicate: true };
  if (records.length >= MAX_QUEUE_ITEMS) {
    await browser.storage.local.set({
      lastError: `Capture queue is full (${MAX_QUEUE_ITEMS} pages). Pair or retry before browsing more pages.`
    });
    return { queued: false, full: true };
  }
  await withStore("readwrite", store => store.add({
    ...batch,
    attempts: 0,
    nextAttemptAt: 0,
    state: "queued",
    lastError: null
  }));
  await updateBadge();
  void drainQueue();
  return { queued: true };
}

async function allRecords() {
  return withStore("readonly", store => requestResult(store.getAll()));
}

async function deleteRecord(batchId) {
  await withStore("readwrite", store => store.delete(batchId));
}

async function updateRecord(record) {
  await withStore("readwrite", store => store.put(record));
}

async function queueStatus() {
  const records = await allRecords();
  return {
    queued: records.filter(item => item.state === "queued").length,
    failed: records.filter(item => item.state === "failed").length
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
    connected: true
  });
  await drainQueue(true);
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
    acceptedItems: 0
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
        capturedAt: record.capturedAt,
        items: record.items
      })
    });
    if (response.ok) {
      const result = await response.json();
      await deleteRecord(record.batchId);
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
    await updateRecord(record);
  } catch (error) {
    record.attempts += 1;
    record.lastError = error.message;
    record.nextAttemptAt = Date.now() + retryDelay(record.attempts);
    await updateRecord(record);
    await browser.storage.local.set({ lastError: error.message });
  }
}

async function drainQueue(force = false) {
  if (drainPromise) return drainPromise;
  drainPromise = (async () => {
    const config = await browser.storage.local.get({ captureEnabled: true, token: null });
    if (!config.captureEnabled || !config.token) return;
    const due = (await allRecords())
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

async function retryFailed() {
  const records = await allRecords();
  for (const record of records.filter(item => item.state === "failed")) {
    record.state = "queued";
    record.attempts = 0;
    record.nextAttemptAt = 0;
    record.lastError = null;
    await updateRecord(record);
  }
  return drainQueue(true);
}

async function updateBadge() {
  const status = await queueStatus();
  const count = status.queued + status.failed;
  await browser.action.setBadgeText({ text: count ? String(Math.min(count, 999)) : "" });
  await browser.action.setBadgeBackgroundColor({ color: status.failed ? "#d32f2f" : "#f59e0b" });
}

browser.runtime.onMessage.addListener(message => {
  switch (message && message.type) {
    case "queue-capture": return queueRecord(message.batch);
    case "pair": return pair(message.serverUrl, message.pairingCode, message.deviceName);
    case "status": return connectionStatus(Boolean(message.checkServer));
    case "retry": return retryFailed().then(() => connectionStatus(false));
    case "set-enabled": return browser.storage.local.set({ captureEnabled: Boolean(message.enabled) })
      .then(() => connectionStatus(false));
    case "forget-pairing": return browser.storage.local.remove([
      "token", "tokenExpiresAt", "source", "connected", "lastError"
    ]);
    default: return undefined;
  }
});

browser.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === DRAIN_ALARM) void drainQueue();
});
browser.runtime.onInstalled.addListener(() => {
  browser.alarms.create(DRAIN_ALARM, { periodInMinutes: 1 });
  void updateBadge();
});
browser.runtime.onStartup.addListener(() => {
  browser.alarms.create(DRAIN_ALARM, { periodInMinutes: 1 });
  void drainQueue();
});
browser.alarms.create(DRAIN_ALARM, { periodInMinutes: 1 });
void updateBadge();
