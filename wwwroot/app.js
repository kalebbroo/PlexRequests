// Small client-side helpers for PlexRequests UI. Kept intentionally tiny; Blazor owns state/logic.
window.plexui = {
    // Smoothly scroll a horizontal media row by ~90% of its visible width. dir: -1 left, +1 right.
    scrollRow: function (el, dir) {
        if (!el) return;
        el.scrollBy({ left: dir * el.clientWidth * 0.9, behavior: 'smooth' });
    }
};
