// Architecture guard for the one-HTTP-boundary invariant (quest 79aa83e7,
// council round-1 amendment): raw fetch() lives ONLY in $lib/api/client.ts,
// and the on401 escape hatch is used only by its named policy owners.
// Comments (JS AND HTML — a .svelte prose comment saying "no new fetch (M7)"
// tripped the first version of this scan) and string literals are stripped
// before scanning; test files are excluded (mocks may do what production
// must not).
import { describe, expect, it } from 'vitest';

const SOURCES = import.meta.glob('/src/**/*.{ts,svelte}', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>;

const FETCH_ALLOWED = new Set(['src/lib/api/client.ts']);
const ON401_ALLOWED = new Set([
  'src/lib/api/client.ts',
  'src/lib/session.svelte.ts',
  'src/lib/presence.svelte.ts',
]);

/** Strip comments (JS + HTML) and string/template literals so prose can't trip the scan. */
function codeOnly(src: string): string {
  return src
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\/\/[^\n]*/g, '')
    .replace(/`(?:\\.|[^`\\])*`/g, '``')
    .replace(/'(?:\\.|[^'\\\n])*'/g, "''")
    .replace(/"(?:\\.|[^"\\\n])*"/g, '""');
}

describe('one HTTP boundary (architecture guard)', () => {
  const files = Object.entries(SOURCES)
    .filter(([path]) => !/\.test\.ts$/.test(path))
    .map(([path, src]) => ({ rel: path.replace(/^\//, ''), code: codeOnly(src) }));

  it('scans a real corpus (a guard over zero input would read as a pass)', () => {
    expect(files.length).toBeGreaterThan(100);
  });

  it('raw fetch() appears only in $lib/api/client.ts', () => {
    const offenders = files
      .filter((f) => !FETCH_ALLOWED.has(f.rel) && /(?<![.\w])fetch\s*\(/.test(f.code))
      .map((f) => f.rel);
    expect(offenders).toEqual([]);
  });

  it('the on401 escape hatch is used only by its named policy owners', () => {
    const offenders = files
      .filter((f) => !ON401_ALLOWED.has(f.rel) && /\bon401\s*:/.test(f.code))
      .map((f) => f.rel);
    expect(offenders).toEqual([]);
  });
});
