import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

// The de-Blazored shell (WP-12: flipped to root). Pure SPA (adapter-static + SPA fallback),
// served by ASP.NET at the site root, talking to the existing /api with the existing auth
// cookie. SvelteKit 3 takes its config here (svelte.config.js is gone). No base path: the app
// is at the root, and every internal link goes through `resolve()` from $app/paths, so setting
// `paths.base` here would still be the only change needed.
export default defineConfig({
  plugins: [
    sveltekit({
      preprocess: vitePreprocess(),
      adapter: adapter({ fallback: 'index.html', strict: false }),
    }),
  ],
  server: {
    port: 5174,
    // Dev loop (de-Blazor WP-03): `npm run dev` serves the SPA on :5174 and proxies /api to the .NET host on
    // :5077 (the HTTP profile in src/FamilyCoordinationApp/Properties/launchSettings.json — the https profile
    // also exposes :7130; NOTHING binds :5000, so a :5000 target would silently connection-refuse the whole
    // dev loop). The FamilyApp.Auth cookie is bound to the .NET origin and is NOT sent to the :5174 dev origin,
    // so the .NET host authenticates the proxied /api calls via the Development-only DevAuthBypassMiddleware
    // (src/FamilyCoordinationApp/Authorization/DevAuthBypassMiddleware.cs). The PROD path (static build served
    // same-origin by .NET under /app) uses the real cookie and needs none of this.
    proxy: {
      '/api': { target: 'http://localhost:5077', changeOrigin: false },
    },
  },
});
