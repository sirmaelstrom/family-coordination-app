import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The connections island (settings cluster B) is embedded into a Razor shell at
// /settings/connections. Build output goes to ./dist/ — the CopyConnectionsIsland
// MSBuild target in FamilyCoordinationApp.csproj copies this into
// wwwroot/islands/connections/. The connections-node-build Docker stage emits dist/,
// the .NET stage reads it via COPY --from. Mirrors frontend/dashboard/vite.config.ts.
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
    port: 5179,
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
