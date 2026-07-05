import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

// The de-Blazored shell (WP-12: flipped to root). Pure SPA (adapter-static + SPA fallback),
// served by ASP.NET at the site root, talking to the existing /api with the existing auth
// cookie. base='' — the /app coexistence prefix died with the Blazor app; every internal nav
// still goes through `base` from $app/paths, so the value lives only here.
/** @type {import('@sveltejs/kit').Config} */
const config = {
  preprocess: vitePreprocess(),
  kit: {
    adapter: adapter({
      fallback: 'index.html',
      strict: false,
    }),
    paths: {
      base: '',
    },
  },
};

export default config;
