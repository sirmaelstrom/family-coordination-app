// Table shape check for UBIQUITOUS_LANGUAGE.md: `node --test scripts/ubiquitous-language.test.mjs`.
// The file is imported into every session through CLAUDE.md, so a row with the wrong number of cells
// (a dropped leading `|`, an unescaped `|` inside a cell) renders as a broken table. Every row of a
// table must have as many cells as its header. UBIQUITOUS_LANGUAGE_PATH points the check at another copy.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const cells = (line) => line.trim().replace(/^\|/, '').replace(/\|$/, '').split(/(?<!\\)\|/);

/** One problem string per table row that does not close with "|" or whose cell count differs from its header. */
export function tableProblems(markdown) {
  const problems = [];
  let header = null;
  markdown.split('\n').forEach((line, i) => {
    if (!line.startsWith('|')) {
      if (header !== null && line.trim() !== '') problems.push(`line ${i + 1}: table row without a leading "|": ${line.slice(0, 50)}`);
      header = null;
      return;
    }
    if (!/\|\s*$/.test(line)) problems.push(`line ${i + 1}: row does not end with "|"`);
    if (header === null) {
      header = cells(line).length;
    } else if (cells(line).length !== header) {
      problems.push(`line ${i + 1}: ${cells(line).length} cells, header has ${header}: ${line.slice(0, 50)}`);
    }
  });
  return problems;
}

const path = process.env.UBIQUITOUS_LANGUAGE_PATH ?? new URL('../UBIQUITOUS_LANGUAGE.md', import.meta.url);

test('every UBIQUITOUS_LANGUAGE.md table row has as many cells as its header', () => {
  assert.deepEqual(tableProblems(readFileSync(path, 'utf8')), []);
});

test('tableProblems flags a dropped inner pipe and a dropped leading pipe, and accepts an escaped pipe', () => {
  const inner = '| A | B | C |\n|---|---|---|\n| x | y |\n| x | y z | w |\n';
  assert.deepEqual(tableProblems(inner).map((p) => p.slice(0, 7)), ['line 3:']);
  const leading = '| A | B |\n|---|---|\n| x | y |\nz | w |\n';
  assert.deepEqual(tableProblems(leading).map((p) => p.slice(0, 7)), ['line 4:']);
  assert.deepEqual(tableProblems('| A | B |\n|---|---|\n| `Week \\| Day` | y |\n'), []);
});
