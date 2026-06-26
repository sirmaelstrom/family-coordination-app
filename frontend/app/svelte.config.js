import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

// Spike: the de-Blazored shell. Pure SPA (adapter-static + SPA fallback), served by ASP.NET
// at the same origin under /app, talking to the existing /api with the existing auth cookie.
// base=/app keeps every asset + router URL under that prefix so it can coexist with the
// Blazor app during the parity-then-flip transition.
/** @type {import('@sveltejs/kit').Config} */
const config = {
  preprocess: vitePreprocess(),
  kit: {
    adapter: adapter({
      fallback: 'index.html',
      strict: false,
    }),
    paths: {
      base: '/app',
    },
  },
};

export default config;
