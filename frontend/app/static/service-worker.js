// Service worker for the de-Blazored SPA shell — root-scoped since the WP-12 flip
// (the Blazor app and its root worker are gone; this one owns /).
// A stable-per-deploy cache version is derived from the script URL so install
// never fails on a stale name.
const CACHE_VERSION = (() => {
  try {
    const u = new URL(self.location.href);
    return u.searchParams.get('v') || u.pathname;
  } catch {
    return 'v1';
  }
})();
const CACHE_NAME = `family-app-spa-${CACHE_VERSION}`;
// Pre-cache the app shell so the SPA works offline. Individual failures must not
// abort install.
const APP_SHELL = ['/', '/manifest.json'];

self.addEventListener('install', (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CACHE_NAME);
      await Promise.allSettled(APP_SHELL.map((a) => cache.add(a)));
      await self.skipWaiting();
    })(),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      const names = await caches.keys();
      await Promise.all(names.filter((n) => n !== CACHE_NAME).map((n) => caches.delete(n)));
      await self.clients.claim();
    })(),
  );
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;

  const url = new URL(req.url);
  if (url.origin !== location.origin) return;

  // Never cache the API — always network (auth + freshness); fall back to cache offline.
  if (url.pathname.startsWith('/api')) {
    event.respondWith(fetch(req).catch(() => caches.match(req)));
    return;
  }

  // Immutable, content-hashed SPA assets (/_app/* is fingerprinted per build): cache-first
  // FOREVER is correct — a new build changes the hash → new URL → cache miss → fetched fresh.
  if (url.pathname.startsWith('/_app/')) {
    event.respondWith(
      caches.match(req).then(
        (cached) =>
          cached ||
          fetch(req).then((res) => {
            const clone = res.clone();
            caches.open(CACHE_NAME).then((c) => c.put(req, clone));
            return res;
          }),
      ),
    );
    return;
  }

  // Stable-NAMED assets (PWA icons): STALE-WHILE-REVALIDATE — serve the cached copy for speed,
  // but ALWAYS refetch in the background and update the cache so a deploy is picked up on the next
  // load. This is the fix for the never-rotating CACHE_VERSION (its `?v=` is never passed by the
  // registration, so the constant version means the activate purge never fires): a changed icon can
  // no longer be pinned to its first-ever copy, WITHOUT relying on a cache-name rotation. Offline: the
  // background refetch just fails and the cached copy stands. (Fingerprinted /_app/* above stays
  // cache-first; only stable-named assets needed this.)
  if (url.pathname.startsWith('/icons/') && /\.(png|svg|webp)$/.test(url.pathname)) {
    event.respondWith(
      caches.match(req).then((cached) => {
        const revalidate = fetch(req)
          .then((res) => {
            const clone = res.clone();
            caches.open(CACHE_NAME).then((c) => c.put(req, clone));
            return res;
          })
          .catch(() => cached);
        return cached || revalidate;
      }),
    );
    return;
  }

  // Network-first for navigations; fall back to the cached app shell offline.
  event.respondWith(
    fetch(req).catch(() => caches.match(req).then((c) => c || caches.match('/'))),
  );
});
