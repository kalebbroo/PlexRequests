// Small client-side helpers for PlexRequests UI. Kept intentionally tiny; Blazor owns state/logic.
window.plexui = {
    // Stream protected downloads through the authenticated Blazor circuit. This avoids turning an
    // expired-cookie redirect into a file that merely has the expected archive filename.
    downloadFileFromStream: async function (fileName, contentType, contentStreamReference) {
        const buffer = await contentStreamReference.arrayBuffer();
        const blob = new Blob([buffer], { type: contentType });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement("a");
        anchor.href = url;
        anchor.download = fileName;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(url);
    },

    // Smoothly scroll a horizontal media row by ~90% of its visible width. dir: -1 left, +1 right.
    scrollRow: function (el, dir) {
        if (!el) return;
        el.scrollBy({ left: dir * el.clientWidth * 0.9, behavior: 'smooth' });
    },

    // Keep same-page discovery links inside the current Blazor component so async content is not
    // torn down and rebuilt before the browser can resolve its fragment target.
    scrollToId: function (id) {
        const target = id ? document.getElementById(id) : null;
        if (!target) return;
        const hash = `#${encodeURIComponent(id)}`;
        const url = `${window.location.pathname}${window.location.search}${hash}`;
        if (window.location.hash === hash) window.history.replaceState(window.history.state, "", url);
        else window.history.pushState(window.history.state, "", url);
        target.scrollIntoView({ behavior: "smooth", block: "start" });
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
