import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The chores island is embedded into a Razor shell at /chores.
// Build output goes to ./dist/ — the CopyChoresIsland MSBuild target in
// FamilyCoordinationApp.csproj copies this into wwwroot/islands/chores/.
// Keeping the output local here makes the Docker build cleaner
// (the chores-node-build stage emits dist/, the .NET stage reads it via COPY --from).
// Mirrors frontend/shopping-list/vite.config.ts (output index.js + index.css).
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
    port: 5174,
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
