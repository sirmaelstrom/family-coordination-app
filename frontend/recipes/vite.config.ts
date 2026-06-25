import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The recipes island is embedded into two Razor shells (Recipes.razor at /recipes
// and RecipeEdit.razor at /recipes/new + /recipes/edit/{id}). Both host pages load
// this single bundle and mount the matching root by a data-view attr (spec D1).
// Build output goes to ./dist/ — the CopyRecipesIsland MSBuild target in
// FamilyCoordinationApp.csproj copies this into wwwroot/islands/recipes/.
// Keeping the output local here makes the Docker build cleaner
// (the recipes-node-build stage emits dist/, the .NET stage reads it via COPY --from).
// Mirrors frontend/meal-plan/vite.config.ts (output index.js + index.css).
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
    port: 5176,
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
