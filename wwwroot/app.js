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
