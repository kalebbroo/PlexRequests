// Small client-side helpers for PlexRequests UI. Kept intentionally tiny; Blazor owns state/logic.
window.plexui = {
    // Smoothly scroll a horizontal media row by ~90% of its visible width. dir: -1 left, +1 right.
    scrollRow: function (el, dir) {
        if (!el) return;
        el.scrollBy({ left: dir * el.clientWidth * 0.9, behavior: 'smooth' });
    },

    _setHeaderOffset: function (selector) {
        const header = selector ? document.querySelector(selector) : null;
        const fallbackHeight = 68;
        const measuredHeight = header && header.getBoundingClientRect ? Math.ceil(header.getBoundingClientRect().height) : fallbackHeight;
        const safeHeight = measuredHeight > 0 ? measuredHeight : fallbackHeight;
        document.documentElement.style.setProperty("--header-offset", `${safeHeight}px`);
    },

    initHeaderMetrics: function (selector) {
        const updateHeaderOffset = () => window.plexui._setHeaderOffset(selector);
        updateHeaderOffset();
        if (window.__plexHeaderOffsetResizeHandler) return;
        window.__plexHeaderOffsetResizeHandler = () => {
            if (window.__plexHeaderOffsetFrame) cancelAnimationFrame(window.__plexHeaderOffsetFrame);
            window.__plexHeaderOffsetFrame = requestAnimationFrame(updateHeaderOffset);
        };
        window.addEventListener("resize", window.__plexHeaderOffsetResizeHandler);
    }
};

// Direct browser-to-server WebSocket for the 1337x Chrome viewer. Frames never pass through the Blazor
// circuit (which would base64-copy them several extra times); Blazor receives only small status changes.
window.plexBrowser = (() => {
    const sessions = new Map();

    function send(session, value) {
        if (session && session.socket.readyState === WebSocket.OPEN)
            session.socket.send(JSON.stringify(value));
    }

    return {
        start: function (canvasId, dotnet) {
            const canvas = document.getElementById(canvasId);
            if (!canvas || sessions.has(canvasId)) return;

            const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
            const socket = new WebSocket(`${scheme}//${window.location.host}/api/admin/indexers/browser`);
            const session = { socket, canvas, dotnet, stopped: false };
            sessions.set(canvasId, session);

            const report = (state, message) => dotnet.invokeMethodAsync("OnBrowserStatus", state, message).catch(() => {});
            socket.onopen = () => report("connected", "Connected to the downloader browser.");
            socket.onerror = () => report("error", "The downloader browser connection failed.");
            socket.onclose = () => {
                sessions.delete(canvasId);
                if (!session.stopped) report("disconnected", "The downloader browser disconnected.");
            };
            socket.onmessage = event => {
                let message;
                try { message = JSON.parse(event.data); } catch { return; }
                if (message.type === "status") {
                    report(message.state || "connected", message.message || "");
                    return;
                }
                if (message.type !== "frame" || !message.data) return;
                const image = new Image();
                image.onload = () => {
                    canvas.width = message.width || image.naturalWidth;
                    canvas.height = message.height || image.naturalHeight;
                    canvas.getContext("2d", { alpha: false }).drawImage(image, 0, 0, canvas.width, canvas.height);
                };
                image.src = `data:image/jpeg;base64,${message.data}`;
            };

            const click = event => {
                const rect = canvas.getBoundingClientRect();
                if (!rect.width || !rect.height) return;
                canvas.focus();
                send(session, {
                    type: "pointer",
                    x: Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width)),
                    y: Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height))
                });
                event.preventDefault();
            };
            const key = event => {
                send(session, { type: "key", key: event.key, code: event.code });
                event.preventDefault();
            };
            session.click = click;
            session.key = key;
            canvas.addEventListener("pointerdown", click);
            canvas.addEventListener("keydown", key);
        },

        command: function (canvasId, command) {
            send(sessions.get(canvasId), { type: command });
        },

        stop: function (canvasId) {
            const session = sessions.get(canvasId);
            if (!session) return;
            session.stopped = true;
            session.canvas.removeEventListener("pointerdown", session.click);
            session.canvas.removeEventListener("keydown", session.key);
            if (session.socket.readyState < WebSocket.CLOSING) session.socket.close(1000, "viewer closed");
            sessions.delete(canvasId);
        }
    };
})();
