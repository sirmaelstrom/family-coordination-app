import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [sveltekit()],
  server: {
    port: 5174,
    // Dev: proxy /api to the running .NET host. NOTE the dev-auth caveat — the FamilyApp.Auth
    // cookie is bound to the .NET origin (:5000/:8080), so the browser will NOT send it to the
    // :5174 dev origin. `npm run dev` therefore needs a dev-auth story (documented in the spike
    // findings). The PROD path (static build served same-origin by .NET) has no such problem.
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: false },
    },
  },
});
