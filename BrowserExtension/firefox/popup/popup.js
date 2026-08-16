"use strict";

const elements = Object.fromEntries([
  "pairing", "connected", "server-url", "pairing-code", "device-name", "pair", "connection-label",
  "source", "accepted", "queued", "failed", "capture-enabled", "retry", "repair", "message", "status-dot"
].map(id => [id, document.getElementById(id)]));

function showMessage(message, error = true) {
  elements.message.textContent = message || "";
  elements.message.style.color = error ? "#ef9a9a" : "#81c784";
}

function render(status) {
  const paired = Boolean(status.serverUrl && status.tokenExpiresAt && new Date(status.tokenExpiresAt) > new Date());
  elements.pairing.hidden = paired;
  elements.connected.hidden = !paired;
  if (!paired) {
    if (status.serverUrl) elements["server-url"].value = status.serverUrl;
    return;
  }
  elements["connection-label"].textContent = status.connected ? "Connected" : "Disconnected";
  elements["status-dot"].style.background = status.connected ? "#4caf50" : "#ef5350";
  elements.source.textContent = status.source || status.serverUrl;
  elements.accepted.textContent = status.acceptedItems || 0;
  elements.queued.textContent = status.queued || 0;
  elements.failed.textContent = status.failed || 0;
  elements["capture-enabled"].checked = status.captureEnabled;
  showMessage(status.lastError, Boolean(status.lastError));
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
    await browser.runtime.sendMessage({
      type: "pair",
      serverUrl: server.origin,
      pairingCode: elements["pairing-code"].value,
      deviceName: elements["device-name"].value
    });
    render(await browser.runtime.sendMessage({ type: "status", checkServer: true }));
    showMessage("Firefox is paired. Browse 1337x normally to capture releases.", false);
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

elements.repair.addEventListener("click", async () => {
  await browser.runtime.sendMessage({ type: "forget-pairing" });
  render(await browser.runtime.sendMessage({ type: "status" }));
  showMessage("Generate a new code in Plex Requests, then pair again.", false);
});

elements["capture-enabled"].addEventListener("change", async event => {
  render(await browser.runtime.sendMessage({ type: "set-enabled", enabled: event.target.checked }));
});

void load();
