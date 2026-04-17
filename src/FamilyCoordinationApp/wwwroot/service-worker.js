// Service Worker for Family Coordination App
// Simple caching strategy for PWA support.
// CACHE_VERSION was historically replaced at build time by a deploy script, but
// the placeholder leaked to prod as a literal string, which broke addAll when
// any listed asset 404'd and left the cache name frozen. We now derive a
// stable-per-deploy version from the SW script URL (ETag-like) and pre-cache
// only assets we KNOW exist, so install never fails.
const CACHE_VERSION = (() => {
  try {
    const u = new URL(self.location.href);
    return u.searchParams.get('v') || u.pathname;
  } catch {
    return 'v1';
  }
})();
const CACHE_NAME = `family-app-${CACHE_VERSION}`;
const STATIC_ASSETS = ['/', '/manifest.json'];

// Install: Cache static assets. Individual failures must not abort install.
self.addEventListener('install', event => {
  event.waitUntil((async () => {
    const cache = await caches.open(CACHE_NAME);
    await Promise.allSettled(STATIC_ASSETS.map(a => cache.add(a)));
    await self.skipWaiting();
  })());
});

// Activate: Clean up old caches
self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys().then(cacheNames => {
      return Promise.all(
        cacheNames
          .filter(name => name !== CACHE_NAME)
          .map(name => caches.delete(name))
      );
    }).then(() => self.clients.claim())
  );
});

// Fetch: Network-first for Blazor, cache-first for static assets
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  
  // Skip non-GET requests
  if (event.request.method !== 'GET') return;
  
  // Skip cross-origin requests
  if (url.origin !== location.origin) return;
  
  // Network-first for Blazor SignalR and API calls
  if (url.pathname.startsWith('/_blazor') || 
      url.pathname.startsWith('/api') ||
      url.pathname.startsWith('/_framework')) {
    event.respondWith(
      fetch(event.request)
        .catch(() => caches.match(event.request))
    );
    return;
  }
  
  // Network-first for CSS (ensures fresh styles after deploy)
  if (url.pathname.endsWith('.css')) {
    event.respondWith(
      fetch(event.request)
        .then(response => {
          const clone = response.clone();
          caches.open(CACHE_NAME).then(cache => cache.put(event.request, clone));
          return response;
        })
        .catch(() => caches.match(event.request))
    );
    return;
  }

  // Cache-first for other static assets (icons, images, fonts)
  if (url.pathname.startsWith('/icons/') ||
      url.pathname.startsWith('/images/') ||
      url.pathname.endsWith('.png') ||
      url.pathname.endsWith('.jpg') ||
      url.pathname.endsWith('.woff2')) {
    event.respondWith(
      caches.match(event.request)
        .then(cached => cached || fetch(event.request).then(response => {
          const clone = response.clone();
          caches.open(CACHE_NAME).then(cache => cache.put(event.request, clone));
          return response;
        }))
    );
    return;
  }
  
  // Network-first for everything else (pages need fresh Blazor state)
  event.respondWith(
    fetch(event.request)
      .catch(() => caches.match(event.request))
  );
});
