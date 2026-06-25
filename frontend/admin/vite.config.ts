import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The admin island (cluster C) is embedded into two Razor shells (HouseholdAdmin.razor
// at /settings/households and FeedbackAdmin.razor at /settings/feedback). Both host pages
// load this single bundle and mount the matching root by a data-view attr (spec D1).
// Build output goes to ./dist/ — the CopyAdminIsland MSBuild target in
// FamilyCoordinationApp.csproj copies this into wwwroot/islands/admin/.
// The admin-node-build Docker stage emits dist/; the .NET stage reads it via COPY --from.
// Mirrors frontend/settings/vite.config.ts (output index.js + index.css).
export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    rollupOptions: {
      input: 'src/main.ts',
      output: {
        entryFileNames: 'index.js',
        chunkFileNames: 'chunks/[name]-[hash].js',
        assetFileNames: (info) =>
          info.name?.endsWith('.css') ? 'index.css' : 'assets/[name]-[hash][extname]',
      },
    },
    sourcemap: true,
  },
  server: {
    port: 5180,
    // Dev server proxies /api to the running .NET app so `npm run dev` works
    // standalone against a locally-running Blazor host on :5000.
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: false,
      },
    },
  },
});
