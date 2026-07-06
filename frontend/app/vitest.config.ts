import { defineConfig } from 'vitest/config';

// Standalone vitest config — deliberately does NOT load the SvelteKit vite plugin.
// The only unit tests here cover rune-free, dependency-free pure modules (e.g.
// src/lib/chores/lib/capacity-fit.ts — the R4′ founding-case gate, V1.6), so the
// default `node` environment is sufficient and avoids the kit dev/build lifecycle.
export default defineConfig({
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});
