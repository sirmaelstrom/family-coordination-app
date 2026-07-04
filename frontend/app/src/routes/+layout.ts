// Pure client-side SPA: no SSR, no prerender. adapter-static emits the SPA fallback (index.html)
// and ASP.NET serves it for every /app/* route; the app boots client-side and fetches /api.
export const ssr = false;
export const prerender = false;
