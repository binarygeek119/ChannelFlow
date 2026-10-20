window.__mediaObserver = window.__mediaObserver || null;
window.__mediaDotNetRef = null;
window.setupMediaInfiniteScroll = (dotNetRef) => {
    window.__mediaDotNetRef = dotNetRef;
    if (window.__mediaObserver) {
        window.__mediaObserver.disconnect();
        window.__mediaObserver = null;
    }
    function attach() {
        const sentinel = document.getElementById('media-sentinel');
        if (!sentinel) return false;
        const obs = new IntersectionObserver(async (entries) => {
            if (entries[0].isIntersecting && window.__mediaDotNetRef) {
                try { await window.__mediaDotNetRef.invokeMethodAsync('TriggerLoadMore'); } catch (e) { console.error('TriggerLoadMore err:', e); }
            }
        }, { rootMargin: '400px' });
        obs.observe(sentinel);
        window.__mediaObserver = obs;
        return true;
    }
    if (!attach()) {
        const mo = new MutationObserver(() => {
            if (attach()) mo.disconnect();
        });
        mo.observe(document.body, { childList: true, subtree: true });
    }
};