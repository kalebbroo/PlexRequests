"use strict";

const elements = Object.fromEntries([
  "pairing", "connected", "server-url", "pairing-code", "device-name", "pair", "connection-label",
  "source", "accepted", "hydrated", "queued", "hydration-queued", "failed", "capture-enabled",
  "resume-hydration", "retry", "repair", "message", "status-dot",
  "update-notice", "update-title", "download-update"
].map(id => [id, document.getElementById(id)]));

function showMessage(message, error = true) {
  elements.message.textContent = message || "";
  elements.message.style.color = error ? "#ef9a9a" : "#81c784";
}

function render(status) {
  const paired = Boolean(status.serverUrl && (status.connections || [])
    .some(connection => !connection.expiresAt || new Date(connection.expiresAt) > new Date()));
  elements.pairing.hidden = paired;
  elements.connected.hidden = !paired;
  if (!paired) {
    if (status.serverUrl) elements["server-url"].value = status.serverUrl;
    return;
  }
  elements["connection-label"].textContent = status.connected ? "Connected" : "Disconnected";
  elements["status-dot"].style.background = status.connected ? "#4caf50" : "#ef5350";
  elements.source.textContent = status.source || status.serverUrl;
  elements["update-notice"].hidden = !status.updateAvailable;
  elements["update-title"].textContent = status.currentExtensionVersion
    ? `Firefox capture ${status.currentExtensionVersion} is available`
    : "Firefox capture update available";
  elements.accepted.textContent = status.acceptedItems || 0;
  elements.hydrated.textContent = status.hydratedItems || 0;
  elements.queued.textContent = status.queued || 0;
  elements["hydration-queued"].textContent = status.hydrationQueued || 0;
  elements.failed.textContent = (status.failed || 0) + (status.hydrationFailed || 0);
  elements["capture-enabled"].checked = status.captureEnabled;
  const paused = status.hydrationPausedUntil && new Date(status.hydrationPausedUntil) > new Date();
  elements["resume-hydration"].hidden = !paused;
  const message = status.lastError || status.hydrationLastError || status.connectionError;
  showMessage(message, Boolean(message));
}

async function load() {
  try { render(await browser.runtime.sendMessage({ type: "status", checkServer: true })); }
  catch (error) { showMessage(error.message); }
}

elements.pair.addEventListener("click", async () => {
  elements.pair.disabled = true;
  showMessage("");
  try {
    const server = new URL(elements["server-url"].value.trim());
    if (server.protocol !== "https:" && !(server.protocol === "http:" && ["localhost", "127.0.0.1"].includes(server.hostname)))
      throw new Error("Use HTTPS, except for a local development server.");
    // Firefox requires this permission request to run directly inside the user's click handler.
    const granted = await browser.permissions.request({ origins: [`${server.origin}/*`] });
    if (!granted) throw new Error("Firefox needs permission to connect to this Plex Requests server.");
    const paired = await browser.runtime.sendMessage({
      type: "pair",
      serverUrl: server.origin,
      pairingCode: elements["pairing-code"].value,
      deviceName: elements["device-name"].value
    });
    render(await browser.runtime.sendMessage({ type: "status", checkServer: true }));
    showMessage(`${paired.source} is paired. Browse that site normally to capture releases.`, false);
  } catch (error) {
    showMessage(error.message);
  } finally {
    elements.pair.disabled = false;
  }
});

elements.retry.addEventListener("click", async () => {
  elements.retry.disabled = true;
  try { render(await browser.runtime.sendMessage({ type: "retry" })); }
  catch (error) { showMessage(error.message); }
  finally { elements.retry.disabled = false; }
});

elements["resume-hydration"].addEventListener("click", async () => {
  elements["resume-hydration"].disabled = true;
  try { render(await browser.runtime.sendMessage({ type: "resume-hydration" })); }
  catch (error) { showMessage(error.message); }
  finally { elements["resume-hydration"].disabled = false; }
});

elements.repair.addEventListener("click", async () => {
  const status = await browser.runtime.sendMessage({ type: "status" });
  if (status.serverUrl) elements["server-url"].value = status.serverUrl;
  elements.pairing.hidden = false;
  elements.connected.hidden = true;
  elements["pairing-code"].focus();
  showMessage("Generate a code from the indexer you want to add or replace.", false);
});

elements["capture-enabled"].addEventListener("change", async event => {
  render(await browser.runtime.sendMessage({ type: "set-enabled", enabled: event.target.checked }));
});

elements["download-update"].addEventListener("click", async () => {
  const status = await browser.runtime.sendMessage({ type: "status" });
  if (status.serverUrl) {
    await browser.tabs.create({ url: `${status.serverUrl}/api/admin/browser-capture/firefox-extension` });
  }
});

void load();
