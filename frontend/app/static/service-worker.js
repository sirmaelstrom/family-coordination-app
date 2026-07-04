// Service worker for the de-Blazored /app SPA shell.
//
// Served at /app/service-worker.js and registered with scope /app/ (see
// app.html) so it ONLY controls the SPA and never intercepts the still-live
// Blazor app at the site root (coexistence, MN8). A stable-per-deploy cache
// version is derived from the script URL so install never fails on a stale name.
const CACHE_VERSION = (() => {
  try {
    const u = new URL(self.location.href);
    return u.searchParams.get('v') || u.pathname;
  } catch {
    return 'v1';
  }
})();
const CACHE_NAME = `family-app-spa-${CACHE_VERSION}`;
// Pre-cache the app shell so /app works offline. Individual failures must not
// abort install.
const APP_SHELL = ['/app', '/app/manifest.json'];

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

  // Cache-first for built SPA assets under /app (js/css/icons/fonts/etc.).
  if (
    url.pathname.startsWith('/app/') &&
    /\.(js|mjs|css|png|jpe?g|svg|webp|woff2?|json)$/.test(url.pathname)
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
    fetch(req).catch(() => caches.match(req).then((c) => c || caches.match('/app'))),
  );
});
