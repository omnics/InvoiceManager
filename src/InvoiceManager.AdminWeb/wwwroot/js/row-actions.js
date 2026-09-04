document.addEventListener("DOMContentLoaded", () => {
    const menus = [...document.querySelectorAll(".row-actions")];
    if (menus.length === 0) return;

    // A fixed-position panel has no containing block to anchor "below the summary button" the
    // way position:absolute did, so its top/left have to be computed from the button's own
    // viewport position - and re-computed on scroll/resize since the button moves but the panel
    // (now outside the scrolling group box) does not move with it automatically.
    const position = (menu) => {
        const summary = menu.querySelector("summary");
        const panel = menu.querySelector(".row-actions-panel");
        if (!summary || !panel) return;

        const buttonRect = summary.getBoundingClientRect();
        const panelWidth = panel.offsetWidth;
        const panelHeight = panel.offsetHeight;
        const margin = 8;

        let left = buttonRect.right - panelWidth;
        left = Math.min(left, window.innerWidth - panelWidth - margin);
        left = Math.max(left, margin);

        let top = buttonRect.bottom + 4;
        if (top + panelHeight > window.innerHeight - margin) {
            top = buttonRect.top - panelHeight - 4;
        }

        panel.style.left = `${left}px`;
        panel.style.top = `${top}px`;
    };

    const close = (menu) => menu.removeAttribute("open");

    menus.forEach(menu => {
        menu.addEventListener("toggle", () => {
            if (!menu.open) return;
            menus.filter(other => other !== menu).forEach(close);
            position(menu);
        });
    });

    document.addEventListener("click", event => {
        menus.forEach(menu => {
            if (menu.open && !menu.contains(event.target)) close(menu);
        });
    });

    document.addEventListener("keydown", event => {
        if (event.key !== "Escape") return;
        menus.forEach(menu => { if (menu.open) close(menu); });
    });

    // Capture phase so scrolling any ancestor (not just the window) is caught too.
    window.addEventListener("scroll", () => {
        menus.forEach(menu => { if (menu.open) position(menu); });
    }, true);
    window.addEventListener("resize", () => {
        menus.forEach(menu => { if (menu.open) position(menu); });
    });
});
