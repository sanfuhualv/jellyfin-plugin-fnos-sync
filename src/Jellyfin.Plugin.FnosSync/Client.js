/* Same-origin Jellyfin Web enhancement. No secrets or user-specific defaults. */
(() => {
    if (window.__fnosWebRefreshInstalled) return;
    window.__fnosWebRefreshInstalled = true;
    window.__fnosWebRefreshVersion = '1.0.0-web-refresh-v3';
    let revision = null, running = false, wake = true, timer;
    let refreshing = false, refreshAgain = false;
    const resumeFetchers = new Map();
    const currentView = () => [...document.querySelectorAll('.view:not(.hide), .page:not(.hide)')].at(-1) || document;
    const visibleIds = () => [...new Set([...currentView().querySelectorAll('[data-id]')].map(e => e.dataset.id).filter(Boolean))].slice(0, 100);
    async function refreshLists() {
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        const x = window.scrollX, y = window.scrollY;
        const focused = document.activeElement?.closest('[data-id]')?.dataset.id;
        try {
            const eligible = () => [...document.querySelectorAll('[is="emby-itemscontainer"]')]
                .filter(e => typeof e.refreshItems === 'function' && /videoplayback|markplayed|markfavorite/.test(e.getAttribute('data-monitor') || '') && !e.closest('.view.hide,.page.hide'));
            let containers = eligible();
            // Hash navigation can precede mounting the target list. Wait for
            // its public items containers rather than losing the refresh.
            if (/^#\/(home|movies|shows|tv|list|favorites|search)/.test(location.hash)) {
                for (let n = 0; !containers.length && n < 20; n++) {
                    await new Promise(resolve => setTimeout(resolve, 150));
                    containers = eligible();
                }
            }
            // ResumeItems has a separate query key in Jellyfin 12.4, which its
            // generic Items invalidation does not cover. Use the official
            // resume REST endpoint with the existing native card renderer.
            for (const box of containers) {
                if (resumeFetchers.has(box) || !/mediaTypes/.test(String(box.fetchData))) continue;
                const monitor = box.getAttribute('data-monitor') || '';
                if (!monitor.includes('videoplayback')) continue;
                const original = box.fetchData;
                resumeFetchers.set(box, original);
                box.fetchData = async () => {
                    const api = window.ApiClient;
                    const url = new URL(api.getUrl('UserItems/Resume'));
                    url.search = new URLSearchParams({ userId: api.getCurrentUserId(), mediaTypes: 'Video',
                        limit: box.classList.contains('scrollSlider') ? '12' : '5',
                        fields: 'PrimaryImageAspectRatio', imageTypeLimit: '1',
                        enableImageTypes: 'Primary,Backdrop,Thumb', enableTotalRecordCount: 'false' });
                    const response = await fetch(url, { headers: { Authorization: 'MediaBrowser Token="' + api.accessToken() + '"' }, cache: 'no-store' });
                    if (!response.ok) throw Error('Resume query failed');
                    return response.json();
                };
            }
            for (const [box] of resumeFetchers) if (!box.isConnected) resumeFetchers.delete(box);
            // Jellyfin 12.4 list fetchers use a query cache. Its own refresh
            // hook must invalidate that cache before asking containers to fetch.
            let invalidate = [...(document._callbacks?.REFRESH_NEEDED || [])];
            for (let n = 0; containers.length && !invalidate.length && n < 8; n++) {
                await new Promise(resolve => setTimeout(resolve, 250));
                invalidate = [...(document._callbacks?.REFRESH_NEEDED || [])];
            }
            await Promise.allSettled(invalidate.map(fn => fn.call(document, { type: 'REFRESH_NEEDED' })));
            await Promise.allSettled(containers.map(e => e.refreshItems()));
            window.scrollTo(x, y);
            if (focused) {
                const card = [...currentView().querySelectorAll('[data-id]')].find(e => e.dataset.id === focused);
                card?.querySelector('a,button')?.focus({ preventScroll: true });
            }
            document.dispatchEvent(new CustomEvent('fnos-sync-refreshed', { detail: { revision, containers: containers.length, invalidateHandlers: invalidate.length } }));
        } finally {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; setTimeout(refreshLists, 200); }
        }
    }
    async function tick() {
        clearTimeout(timer);
        if (running || document.hidden) { timer = setTimeout(tick, 1000); return; }
        const api = window.ApiClient;
        if (!api?.accessToken?.()) { timer = setTimeout(tick, 2000); return; }
        running = true;
        let delay = 1000;
        try {
            const requestWake = wake; wake = false;
            const query = new URLSearchParams({ wake: String(requestWake), ids: requestWake ? visibleIds().join(',') : '' });
            const response = await fetch(api.getUrl('FnosSync/ClientState') + '?' + query, {
                headers: { Authorization: 'MediaBrowser Token="' + api.accessToken() + '"' }, cache: 'no-store'
            });
            if (!response.ok) { delay = 5000; return; }
            const state = await response.json();
            if (!state.enabled) {
                for (const [box, original] of resumeFetchers) box.fetchData = original;
                resumeFetchers.clear(); revision = null; delay = 10000; return;
            }
            const changed = revision === null ? state.revision > 0 : state.revision !== revision;
            revision = state.revision;
            if (changed || requestWake) await refreshLists();
        } catch { delay = 5000; }
        finally { running = false; timer = setTimeout(tick, delay); }
    }
    const opened = () => { wake = true; clearTimeout(timer); timer = setTimeout(tick, 150); };
    window.addEventListener('hashchange', opened);
    document.addEventListener('viewshow', opened, true);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) opened(); });
    timer = setTimeout(tick, 500);
})();
