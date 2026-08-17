import { svelte } from '@sveltejs/vite-plugin-svelte';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

// Standalone vitest config — the bare svelte plugin (NOT sveltekit()) compiles `.svelte` /
// `.svelte.ts` rune modules so stores are unit-testable, without the kit dev/build lifecycle.
// Kit-specific module ids ($app/*) are not provided here: a store that imports them needs its
// test to mock them. `$lib` is aliased manually below (the kit plugin would normally supply it).
// The `node` environment is deliberate — rune MODULES need compilation, not a DOM; component
// mounting tests would need a DOM package, which is a separate decision (lockfile trap:
// auto-memory windows-lockfile-linux-optional-deps-eusage).
export default defineConfig({
  plugins: [svelte()],
  resolve: {
    alias: {
      $lib: fileURLToPath(new URL('./src/lib', import.meta.url)),
    },
  },
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});
