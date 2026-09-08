const states = new WeakMap();

export function show(dialog) {
    if (states.has(dialog)) return;
    const cancel = event => event.preventDefault();
    const overflow = document.documentElement.style.overflow;
    const focus = document.activeElement;
    dialog.addEventListener("cancel", cancel);
    dialog.showModal();
    document.documentElement.style.overflow = "hidden";
    dialog.focus({ preventScroll: true });
    const observer = new MutationObserver(() => {
        if (!dialog.isConnected) close(dialog);
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
    states.set(dialog, { cancel, overflow, focus, observer });
}

export function close(dialog) {
    const state = states.get(dialog);
    if (!state) return;
    state.observer.disconnect();
    dialog.removeEventListener("cancel", state.cancel);
    dialog.close();
    document.documentElement.style.overflow = state.overflow;
    if (state.focus?.isConnected) state.focus.focus({ preventScroll: true });
    states.delete(dialog);
}
