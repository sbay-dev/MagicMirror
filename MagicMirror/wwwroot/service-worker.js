// 🧬 Cepha PWA Service Worker
// Network-first for HTML/JS (ensures fresh fingerprints + import maps)
// Cache-first for immutable fingerprinted assets (*.{hash}.ext)
const CACHE_NAME = 'cepha-v2';

self.addEventListener('install', e => {
    self.skipWaiting();
});

self.addEventListener('activate', e => {
    e.waitUntil(
        caches.keys().then(keys =>
            Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k)))
        )
    );
    self.clients.claim();
});

// Fingerprinted file pattern: name.{10+ char hash}.ext
const FINGERPRINTED = /\.\w{10,}\.\w+$/;

self.addEventListener('fetch', e => {
    const url = new URL(e.request.url);
    if (url.origin !== location.origin || e.request.method !== 'GET') return;

    // Fingerprinted assets are immutable — cache-first
    if (FINGERPRINTED.test(url.pathname)) {
        e.respondWith(
            caches.match(e.request).then(cached => {
                if (cached) return cached;
                return fetch(e.request).then(res => {
                    if (res.ok) {
                        const clone = res.clone();
                        caches.open(CACHE_NAME).then(c => c.put(e.request, clone));
                    }
                    return res;
                });
            })
        );
        return;
    }

    // Everything else (HTML, non-fingerprinted JS, CSS) — network-first
    e.respondWith(
        fetch(e.request).then(res => {
            if (res.ok) {
                const clone = res.clone();
                caches.open(CACHE_NAME).then(c => c.put(e.request, clone));
            }
            return res;
        }).catch(() => caches.match(e.request))
    );
});