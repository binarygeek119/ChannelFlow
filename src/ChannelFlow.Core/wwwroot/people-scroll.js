window.setupPeopleInfiniteScroll = (dotNetRef) => {
    const sentinel = document.getElementById('people-sentinel');
    if (!sentinel) return;
    const observer = new IntersectionObserver(async (entries) => {
        if (entries[0].isIntersecting) {
            await dotNetRef.invokeMethodAsync('TriggerLoadMore');
        }
    }, { rootMargin: '400px' });
    observer.observe(sentinel);
};
