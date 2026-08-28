window.scheduleApp = window.scheduleApp || {};

window.scheduleApp.navigationEscapeHandlers = window.scheduleApp.navigationEscapeHandlers || new Map();
window.scheduleApp.navigationEscapeSequence = window.scheduleApp.navigationEscapeSequence || 0;

window.scheduleApp.focusMainContent = () => {
    const main = document.getElementById("main-content");
    if (!main) return;
    const navbar = document.querySelector(".app-navbar");
    const stickyOffset = navbar instanceof HTMLElement
        ? navbar.getBoundingClientRect().height + 8
        : 0;
    const targetTop = Math.max(0, window.scrollY + main.getBoundingClientRect().top - stickyOffset);
    main.focus({ preventScroll: true });
    window.scrollTo({ top: targetTop, behavior: "auto" });
    history.replaceState(history.state, "", `${location.pathname}${location.search}#main-content`);
};

window.scheduleApp.registerNavigationEscape = dotNetReference => {
    const listenerId = ++window.scheduleApp.navigationEscapeSequence;
    const handler = event => {
        if (event.key !== "Escape") return;
        void dotNetReference.invokeMethodAsync("HandleDocumentEscapeAsync").catch(() => {});
    };
    document.addEventListener("keydown", handler, true);
    window.scheduleApp.navigationEscapeHandlers.set(listenerId, handler);
    return listenerId;
};

window.scheduleApp.unregisterNavigationEscape = listenerId => {
    const handler = window.scheduleApp.navigationEscapeHandlers.get(listenerId);
    if (!handler) return;
    document.removeEventListener("keydown", handler, true);
    window.scheduleApp.navigationEscapeHandlers.delete(listenerId);
};

window.scheduleApp.focusModal = element => {
    if (!element || element.dataset.focusTrapAttached === "true") return;
    element.dataset.focusTrapAttached = "true";
    const previousFocus = document.activeElement;
    const focusableSelector = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
    element.addEventListener("keydown", event => {
        if (event.key !== "Tab") return;
        const focusable = Array.from(element.querySelectorAll(focusableSelector))
            .filter(candidate => candidate.getClientRects().length > 0
                && !candidate.matches(":disabled")
                && !candidate.closest("[inert]"));
        if (focusable.length === 0) {
            event.preventDefault();
            element.focus();
            return;
        }
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (event.shiftKey && (document.activeElement === first || document.activeElement === element)) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    });
    const observer = new MutationObserver(() => {
        if (element.isConnected) return;
        observer.disconnect();
        if (previousFocus instanceof HTMLElement && previousFocus.isConnected) previousFocus.focus();
    });
    observer.observe(document.body, { childList: true, subtree: true });
    element.focus();
};
