"use strict";

const sources = globalThis.PlexRequestsCaptureSources;
const hydration = globalThis.PlexRequestsHydration;
const telemetry = globalThis.PlexRequestsCaptureTelemetry;
const DATABASE_NAME = "plexrequests-browser-capture";
const DATABASE_VERSION = 2;
const CAPTURE_STORE_NAME = "captureQueue";
const HYDRATION_STORE_NAME = "hydrationQueue";
const DRAIN_ALARM = "drain-capture-queue";
const HYDRATION_ALARM = "drain-hydration-queue";
const HEARTBEAT_ALARM = "report-capture-health";
const BACKLOG_ALARM = "reconcile-server-backlog";
const MAX_QUEUE_ITEMS = 2000;
const MAX_HYDRATION_ITEMS = 2000;
const HYDRATION_TIMEOUT_MS = 90_000;
const HYDRATION_CHALLENGE_PAUSE_MS = 15 * 60 * 1000;
const COMPLETED_HYDRATION_RETENTION_MS = 7 * 24 * 60 * 60 * 1000;
let drainPromise = null;
let hydrationPromise = null;
let heartbeatPromise = null;
let backlogPromise = null;
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
  const retainedById = new Map(retained.map(item => [item.externalId, item]));
  const retainedIds = new Set(retained.map(item => item.externalId));
  const pendingCount = retained.filter(item => item.state !== "complete").length;
  const available = Math.max(0, MAX_HYDRATION_ITEMS - pendingCount);
  const candidateMap = new Map();
  for (const item of items || []) {
    const candidate = hydration.detailCandidate(item, now);
    if (candidate) candidateMap.set(candidate.externalId, candidate);
  }
  const newCandidates = [...candidateMap.values()].filter(candidate => !retainedIds.has(candidate.externalId));
  const candidates = newCandidates.slice(0, available);
  const refreshed = [...candidateMap.values()].filter(candidate => {
    const existing = retainedById.get(candidate.externalId);
    return existing && existing.state !== "complete"
      && (candidate.capturePageToken || candidate.sourceUrl !== existing.sourceUrl);
  }).map(candidate => ({ ...retainedById.get(candidate.externalId), ...candidate }));

  await withStore(HYDRATION_STORE_NAME, "readwrite", store => {
    for (const record of records) {
      if (!retainedIds.has(record.externalId)) store.delete(record.externalId);
    }
    for (const candidate of candidates) store.put(candidate);
    for (const candidate of refreshed) store.put(candidate);
  });

  if (newCandidates.length > candidates.length) {
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
    browser.storage.local.get({ hydrationPausedUntil: null, hydrationPauses: {} })
  ]);
  const now = Date.now();
  const pauseEntries = Object.entries(state.hydrationPauses || {})
    .filter(([, until]) => Number(until) > now);
  const pauseTimes = pauseEntries.map(([, until]) => Number(until));
  if (Number(state.hydrationPausedUntil) > now) pauseTimes.push(Number(state.hydrationPausedUntil));
  const hydrationPausedUntil = pauseTimes.length ? Math.max(...pauseTimes) : null;
  const hydrationNeedsAttention = details.filter(item => item.state === "failed" || item.needsAttention).length;
  return {
    queued: captures.filter(item => item.state === "queued").length,
    failed: captures.filter(item => item.state === "failed").length,
    hydrationQueued: details.filter(item => (item.state === "queued" || item.state === "loading") && !item.needsAttention).length,
    hydrationFailed: hydrationNeedsAttention,
    hydrationNeedsAttention,
    hydrationPausedUntil,
    hydrationPausedSources: pauseEntries.map(([sourceKey]) => sourceKey)
  };
}

function normalizedServerUrl(value) {
  const url = new URL(String(value || "").trim());
  if (url.protocol !== "https:" && !(url.protocol === "http:" && ["localhost", "127.0.0.1"].includes(url.hostname))) {
    throw new Error("Use HTTPS, except for a local development server.");
  }
  return url.origin;
}

async function connectionConfig() {
  const config = await browser.storage.local.get({
    serverUrl: "",
    connections: {},
    token: null,
    tokenExpiresAt: null,
    source: ""
  });
  if (config.token && !Object.keys(config.connections || {}).length) {
    const legacyKey = /ext\.to/i.test(config.source) ? "ext.to" : "1337x";
    config.connections = {
      [legacyKey]: {
        token: config.token,
        expiresAt: config.tokenExpiresAt,
        source: config.source || sources.byKey(legacyKey)?.displayName || legacyKey,
        connected: true,
        lastError: null
      }
    };
    await browser.storage.local.set({ connections: config.connections });
    await browser.storage.local.remove(["token", "tokenExpiresAt", "source", "connected"]);
  }
  return config;
}

async function updateConnection(sourceKey, changes) {
  const config = await connectionConfig();
  const current = config.connections[sourceKey];
  if (!current) return;
  config.connections[sourceKey] = { ...current, ...changes };
  await browser.storage.local.set({ connections: config.connections });
}

async function apiFetch(path, options = {}, sourceKey = null) {
  const config = await connectionConfig();
  if (!config.serverUrl) throw new Error("Pair this extension with Plex Requests first.");
  const headers = new Headers(options.headers || {});
  headers.set("Accept", "application/json");
  if (options.body) headers.set("Content-Type", "application/json");
  if (sourceKey) {
    const connection = config.connections[sourceKey];
    if (!connection?.token) {
      const name = sources.byKey(sourceKey)?.displayName || sourceKey;
      throw new Error(`Pair ${name} from its indexer row before uploading captured pages.`);
    }
    headers.set("Authorization", `Bearer ${connection.token}`);
  }
  return fetch(`${config.serverUrl}${path}`, { ...options, headers });
}

async function pair(serverUrl, pairingCode, deviceName) {
  const server = normalizedServerUrl(serverUrl);
  const originPermission = `${server}/*`;
  const granted = await browser.permissions.contains({ origins: [originPermission] });
  if (!granted) throw new Error("Firefox needs permission to connect to this Plex Requests server.");

  const previous = await connectionConfig();
  const response = await fetch(`${server}/api/browser-capture/pair`, {
    method: "POST",
    headers: { "Accept": "application/json", "Content-Type": "application/json" },
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
  const sourceKey = String(paired.implementation || "").trim().toLowerCase();
  if (!sources.byKey(sourceKey)) throw new Error("The server paired an unsupported capture source.");
  const connections = previous.serverUrl && previous.serverUrl !== server ? {} : previous.connections || {};
  connections[sourceKey] = {
    token: paired.token,
    expiresAt: paired.expiresAt,
    source: paired.source,
    connected: true,
    lastError: null
  };
  await browser.storage.local.set({
    serverUrl: server,
    connections,
    lastError: null,
    hydrationLastError: null
  });
  void reportHeartbeats();
  void reconcileBacklog();
  await drainQueue(true);
  void drainHydrationQueue(true);
  return paired;
}

async function connectionStatus(checkServer = false) {
  const state = await browser.storage.local.get({
    captureEnabled: true,
    serverUrl: "",
    lastError: null,
    lastSuccessAt: null,
    acceptedItems: 0,
    hydratedItems: 0,
    hydrationLastError: null,
    hydrationPausedUntil: null
  });
  const config = await connectionConfig();
  const connections = config.connections || {};
  if (checkServer && config.serverUrl) {
    for (const [sourceKey, connection] of Object.entries(connections)) {
      try {
        const response = await apiFetch("/api/browser-capture/status", {}, sourceKey);
        connection.connected = response.ok;
        connection.lastError = response.ok ? null : response.status === 401
          ? "Pairing expired or was revoked."
          : `Server returned HTTP ${response.status}.`;
      } catch (error) {
        connection.connected = false;
        connection.lastError = error.message;
      }
    }
    await browser.storage.local.set({ connections });
  }
  const connectionList = Object.entries(connections).map(([sourceKey, connection]) => ({
    sourceKey,
    ...connection
  }));
  return {
    ...state,
    serverUrl: config.serverUrl,
    connections: connectionList,
    connected: connectionList.some(connection => connection.connected),
    source: connectionList.map(connection => connection.source).filter(Boolean).join(" · "),
    tokenExpiresAt: connectionList.map(connection => connection.expiresAt).filter(Boolean).sort().at(-1) || null,
    connectionError: connectionList.find(connection => connection.lastError)?.lastError || null,
    ...(await queueStatus())
  };
}

async function reportHeartbeats() {
  if (heartbeatPromise) return heartbeatPromise;
  heartbeatPromise = (async () => {
    const [captures, details, state, config] = await Promise.all([
      allRecords(CAPTURE_STORE_NAME),
      allRecords(HYDRATION_STORE_NAME),
      browser.storage.local.get({
        captureEnabled: true,
        hydrationPausedUntil: null,
        hydrationPauses: {}
      }),
      connectionConfig()
    ]);
    if (!config.serverUrl) return;

    for (const [sourceKey, connection] of Object.entries(config.connections || {})) {
      if (!connection?.token) continue;
      const snapshot = telemetry.queueSnapshot(sourceKey, captures, details, state);
      try {
        const response = await apiFetch("/api/browser-capture/heartbeat", {
          method: "POST",
          body: JSON.stringify({
            ...snapshot,
            hydrationPausedUntil: snapshot.hydrationPausedUntil
              ? new Date(snapshot.hydrationPausedUntil).toISOString()
              : null,
            extensionVersion: browser.runtime.getManifest().version
          })
        }, sourceKey);
        await updateConnection(sourceKey, {
          connected: response.ok,
          lastError: response.ok ? null : response.status === 401
            ? "Pairing expired or was revoked."
            : `Health report returned HTTP ${response.status}.`
        });
      } catch (error) {
        await updateConnection(sourceKey, { connected: false, lastError: error.message });
      }
    }
  })().finally(() => { heartbeatPromise = null; });
  return heartbeatPromise;
}

async function reconcileBacklog() {
  if (backlogPromise) return backlogPromise;
  backlogPromise = (async () => {
    const [config, state, details] = await Promise.all([
      connectionConfig(),
      browser.storage.local.get({ captureEnabled: true, backlogCursors: {} }),
      allRecords(HYDRATION_STORE_NAME)
    ]);
    if (!state.captureEnabled || !config.serverUrl) return;

    const backlogCursors = { ...(state.backlogCursors || {}) };
    for (const [sourceKey, connection] of Object.entries(config.connections || {})) {
      if (!connection?.token || !sources.byKey(sourceKey)?.durableBacklog) continue;
      const capacity = hydration.backlogCapacity(details, MAX_HYDRATION_ITEMS, 250);
      if (!capacity) break;

      const cursor = Math.max(0, Number(backlogCursors[sourceKey]) || 0);
      try {
        const response = await apiFetch(
          `/api/browser-capture/pending-details?after=${cursor}&limit=${Math.min(250, capacity)}`,
          {},
          sourceKey);
        if (!response.ok) {
          await updateConnection(sourceKey, {
            connected: false,
            lastError: response.status === 401
              ? "Pairing expired or was revoked."
              : `Backlog recovery returned HTTP ${response.status}.`
          });
          continue;
        }

        const page = await response.json();
        const items = Array.isArray(page.items) ? page.items : [];
        if (items.length) {
          await enqueueHydrations(items);
        }
        // Wrap after reaching the end (or receiving a malformed cursor) so work cannot be stranded.
        backlogCursors[sourceKey] = hydration.nextBacklogCursor(cursor, page);
        await updateConnection(sourceKey, { connected: true, lastError: null });
      } catch (error) {
        await updateConnection(sourceKey, { connected: false, lastError: error.message });
      }
    }
    await browser.storage.local.set({ backlogCursors });
    void drainHydrationQueue();
  })().finally(() => { backlogPromise = null; });
  return backlogPromise;
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

function publicCatalogItems(items) {
  return (items || []).map(({ captureTorrentId, capturePageToken, captureSessionId, ...item }) => item);
}

async function sha256Hex(value) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
}

async function resolveExtMagnet(record) {
  if (!record.captureTorrentId || !record.capturePageToken || !record.captureSessionId) {
    throw new Error("EXT.to page credentials expired or were unavailable; revisit the listing to refresh them.");
  }
  const timestamp = Math.floor(Date.now() / 1000);
  const hmac = await sha256Hex(`${record.captureTorrentId}|${timestamp}|${record.capturePageToken}`);
  const endpoint = new URL("/ajax/getSearchMagnet.php", record.sourceUrl);
  const response = await fetch(endpoint.toString(), {
    method: "POST",
    credentials: "include",
    headers: {
      "Accept": "application/json",
      "Content-Type": "application/x-www-form-urlencoded",
      "X-Requested-With": "XMLHttpRequest"
    },
    body: new URLSearchParams({
      torrent_id: String(record.captureTorrentId),
      hash: "",
      name: "",
      timestamp: String(timestamp),
      hmac,
      sessid: record.captureSessionId
    }).toString()
  });
  if (!response.ok) throw new Error(`EXT.to magnet lookup returned HTTP ${response.status}.`);
  const body = await response.json();
  if (!body.success || typeof body.url !== "string" || !body.url.startsWith("magnet:")) {
    throw new Error("EXT.to did not return a usable magnet; revisit the listing after completing any challenge.");
  }
  const infoHash = body.url.match(/(?:^|[?&])xt=urn:btih:([a-z0-9]+)/i)?.[1] || null;
  const item = {
    externalId: record.externalId,
    releaseName: record.releaseName,
    sourceUrl: record.sourceUrl,
    infoHash,
    magnetUri: body.url,
    category: record.category,
    uploader: record.uploader,
    seeders: record.seeders,
    leechers: record.leechers,
    sizeBytes: record.sizeBytes,
    publishedAt: record.publishedAt,
    needsHydration: false
  };
  const fingerprint = await sha256Hex(`ext.to:${record.externalId}:${infoHash || body.url}`);
  const queued = await queueRecord({
    batchId: `firefox-v2-${fingerprint}`,
    sourceKey: "ext.to",
    pageUrl: record.sourceUrl,
    pageType: "detail",
    parserVersion: 2,
    capturedAt: new Date().toISOString(),
    items: [item]
  });
  if (!queued.queued && !queued.duplicate) {
    throw new Error("The upload queue is full; EXT.to detail resolution will retry after pending uploads drain.");
  }
  await completeHydrations([item]);
}

async function sendRecord(record) {
  const sourceKey = record.sourceKey || sources.fromUrl(record.pageUrl)?.key;
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
        items: publicCatalogItems(record.items)
      })
    }, sourceKey);
    if (response.ok) {
      const result = await response.json();
      await deleteRecord(CAPTURE_STORE_NAME, record.batchId);
      const state = await browser.storage.local.get({ acceptedItems: 0 });
      await browser.storage.local.set({
        lastError: null,
        lastSuccessAt: new Date().toISOString(),
        acceptedItems: state.acceptedItems + (result.duplicateBatch ? 0 : result.acceptedItems)
      });
      await updateConnection(sourceKey, { connected: true, lastError: null });
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
      await updateConnection(sourceKey, { connected: false, lastError: "Pairing expired or was revoked." });
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
    const state = await browser.storage.local.get({ captureEnabled: true });
    const config = await connectionConfig();
    if (!state.captureEnabled || !Object.keys(config.connections || {}).length) return;
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
    record.needsAttention = false;
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
    hydrationWorkerSourceKey: null,
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
    "hydrationWorkerTabId", "hydrationWorkerExternalId", "hydrationWorkerSourceKey", "hydrationWorkerStartedAt"
  ]);
  if (closeTab && await existingTab(state.hydrationWorkerTabId)) {
    try { await browser.tabs.remove(state.hydrationWorkerTabId); } catch { /* already closed */ }
  }
  return state;
}

async function retryHydration(externalId, error, delay = null, attentionOverride = null) {
  if (!externalId) return;
  const records = await allRecords(HYDRATION_STORE_NAME);
  const record = records.find(item => item.externalId === externalId);
  if (!record || record.state === "complete") return;
  record.startedAt = null;
  record.lastError = error;
  record.state = "queued";
  record.needsAttention = attentionOverride ?? hydration.needsAttention(error, record.attempts);
  const retryIn = delay ?? (record.needsAttention
    ? hydration.attentionRetryDelay()
    : hydration.retryDelay(record.attempts));
  record.nextAttemptAt = Date.now() + retryIn;
  await updateRecord(HYDRATION_STORE_NAME, record);
  await browser.storage.local.set({ hydrationLastError: error });
}

async function reviveStrandedHydrations(activeExternalId = null) {
  const records = await allRecords(HYDRATION_STORE_NAME);
  const now = Date.now();
  const changed = [];
  const normalized = records.map(record => {
    const revivedFailed = hydration.reviveLegacyFailure(record, now);
    const revived = hydration.reviveInterrupted(revivedFailed, activeExternalId, now);
    if (revived !== record) changed.push(revived);
    return revived;
  });
  if (changed.length) {
    await withStore(HYDRATION_STORE_NAME, "readwrite", store => {
      for (const record of changed) store.put(record);
    });
  }
  return normalized;
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
    hydrationWorkerSourceKey: null,
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
    hydrationWorkerSourceKey: null,
    hydrationWorkerStartedAt: null
  });
  await updateBadge();
  scheduleHydration();
}

async function pauseForChallenge(tabId) {
  const state = await workerState();
  if (state.hydrationWorkerTabId !== tabId) return;
  const sourceName = sources.byKey(state.hydrationWorkerSourceKey)?.displayName || "The indexer";
  const pausedUntil = Date.now() + HYDRATION_CHALLENGE_PAUSE_MS;
  await retryHydration(
    state.hydrationWorkerExternalId,
    `${sourceName} presented a browser challenge. Automatic detail capture is paused; open it normally, complete the challenge, then resume.`,
    HYDRATION_CHALLENGE_PAUSE_MS,
    true
  );
  const pauseState = await browser.storage.local.get({ hydrationPauses: {} });
  const hydrationPauses = { ...(pauseState.hydrationPauses || {}) };
  if (state.hydrationWorkerSourceKey) hydrationPauses[state.hydrationWorkerSourceKey] = pausedUntil;
  await browser.storage.local.set({
    hydrationPauses,
    hydrationPausedUntil: null,
    hydrationLastError: `${sourceName} challenge detected. Complete it in a normal tab, then resume detail capture.`
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
    const state = await browser.storage.local.get({
      captureEnabled: true,
      hydrationPausedUntil: null,
      hydrationPauses: {}
    });
    const config = await connectionConfig();
    const activeSources = new Set(Object.entries(config.connections || {})
      .filter(([, connection]) => !connection.expiresAt || new Date(connection.expiresAt) > new Date())
      .map(([sourceKey]) => sourceKey));
    if (!state.captureEnabled || !activeSources.size) return;
    const now = Date.now();
    let hydrationPauses = { ...(state.hydrationPauses || {}) };
    if (Number(state.hydrationPausedUntil) > now && !Object.keys(hydrationPauses).length) {
      for (const sourceKey of activeSources) hydrationPauses[sourceKey] = Number(state.hydrationPausedUntil);
    }
    hydrationPauses = hydration.activePauses(hydrationPauses, now);
    if (force) hydrationPauses = {};
    await browser.storage.local.set({
      hydrationPauses,
      hydrationPausedUntil: null,
      ...(force ? { hydrationLastError: null } : {})
    });
    const eligibleSources = hydration.eligibleSources(activeSources, hydrationPauses, now);
    if (!eligibleSources.size) {
      await updateBadge();
      return;
    }

    const current = await recoverStaleWorker();
    const records = await reviveStrandedHydrations(current.hydrationWorkerExternalId);
    if (current.hydrationWorkerExternalId) return;
    const next = hydration.nextDue(records, Date.now(), eligibleSources);
    if (!next) {
      if (await existingTab(current.hydrationWorkerTabId)) await clearWorker(true);
      return;
    }

    next.state = "loading";
    next.attempts += 1;
    next.startedAt = Date.now();
    next.lastError = null;
    await updateRecord(HYDRATION_STORE_NAME, next);

    if (next.sourceKey === "ext.to") {
      try {
        await resolveExtMagnet(next);
        await browser.storage.local.set({ hydrationLastError: null });
      } catch (error) {
        await retryHydration(next.externalId, error.message);
      }
      await updateBadge();
      scheduleHydration();
      return;
    }

    try {
      let tab = await existingTab(current.hydrationWorkerTabId);
      if (!tab) tab = await browser.tabs.create({ url: "about:blank", active: false });
      await browser.storage.local.set({
        hydrationWorkerTabId: tab.id,
        hydrationWorkerExternalId: next.externalId,
        hydrationWorkerSourceKey: next.sourceKey || sources.fromUrl(next.sourceUrl)?.key || null,
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
    record.needsAttention = false;
    record.startedAt = null;
    record.nextAttemptAt = 0;
    record.lastError = null;
    await updateRecord(HYDRATION_STORE_NAME, record);
  }
  await browser.storage.local.set({ hydrationPausedUntil: null, hydrationPauses: {}, hydrationLastError: null });
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
    await retryHydration(state.hydrationWorkerExternalId, "Detail capture paused.", 0, false);
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
        "token", "tokenExpiresAt", "source", "connected", "connections", "lastError",
        "hydrationPausedUntil", "hydrationPauses", "hydrationLastError", "backlogCursors"
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
  if (alarm.name === HEARTBEAT_ALARM) void reportHeartbeats();
  if (alarm.name === BACKLOG_ALARM) void reconcileBacklog();
});

function createAlarms() {
  browser.alarms.create(DRAIN_ALARM, { periodInMinutes: 1 });
  browser.alarms.create(HYDRATION_ALARM, { periodInMinutes: 1 });
  browser.alarms.create(HEARTBEAT_ALARM, { periodInMinutes: 1 });
  browser.alarms.create(BACKLOG_ALARM, { periodInMinutes: 5 });
}

browser.runtime.onInstalled.addListener(() => {
  createAlarms();
  void updateBadge();
  void drainHydrationQueue();
  void reportHeartbeats();
  void reconcileBacklog();
});
browser.runtime.onStartup.addListener(() => {
  createAlarms();
  void drainQueue();
  void drainHydrationQueue();
  void reportHeartbeats();
  void reconcileBacklog();
});
createAlarms();
void updateBadge();
void drainHydrationQueue();
void reportHeartbeats();
void reconcileBacklog();
