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

  // Cache-first ONLY for immutable/fingerprinted SPA assets (/_app/* is content-hashed
  // per build) and the static icons. Everything else — including the server-rendered
  // Razor pages (/account/*, /household/*, legal) and their assets — stays network-first
  // so a deploy is picked up immediately.
  if (
    url.pathname.startsWith('/_app/') ||
    (url.pathname.startsWith('/icons/') && /\.(png|svg|webp)$/.test(url.pathname))
  ) {
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

  // Network-first for navigations; fall back to the cached app shell offline.
  event.respondWith(
    fetch(req).catch(() => caches.match(req).then((c) => c || caches.match('/'))),
  );
});
