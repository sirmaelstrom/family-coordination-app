// Self-test for the npm audit filter: `node --test scripts/`.
// kit2-master.json and kit3-picomatch-only.json are real `npm audit --json` output, captured
// 2026-11-17 from master's lockfile (SvelteKit 2.70) and from the SvelteKit 3 lockfile.
// The accept/stale rules are held with LISTED: the picomatch entry the live list carried until
// GitHub withdrew GHSA-fc5q-39xc-vjpf on 2026-11-18. The live list itself is empty.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { ACCEPTED_ADVISORIES, evaluateNpmAudit } from './npm-audit-filter.mjs';

const fixture = (name) => readFileSync(new URL(`./fixtures/npm-audit/${name}`, import.meta.url), 'utf8');
const PICOMATCH = 'GHSA-fc5q-39xc-vjpf';
const LISTED = [{ id: PICOMATCH, package: 'picomatch', why: 'test-only accepted entry' }];

/** A minimal report: `advisories` are [package, ghsa, severity] triples, `inherits` are [package, via-package]. */
function report(advisories, inherits = []) {
  const vulnerabilities = {};
  for (const [name, id, severity] of advisories) {
    (vulnerabilities[name] ??= { name, severity, via: [] }).via.push({
      name, title: `${name} advisory`, url: `https://github.com/advisories/${id}`, severity, range: '<1.0.0',
    });
  }
  for (const [name, via] of inherits) {
    (vulnerabilities[name] ??= { name, severity: vulnerabilities[via]?.severity ?? 'high', via: [] }).via.push(via);
  }
  return JSON.stringify({ auditReportVersion: 2, vulnerabilities, metadata: {} });
}

test('the accepted list is empty', () => {
  assert.deepEqual(ACCEPTED_ADVISORIES, []);
});

test('a clean audit passes on the live (empty) list', () => {
  const v = evaluateNpmAudit(report([]));
  assert.equal(v.ok, true, v.problems.join('\n'));
  assert.deepEqual(v.stale, []);
});

test('the real Kit 3 audit (picomatch only) fails on the live list: nothing is accepted any more', () => {
  const v = evaluateNpmAudit(fixture('kit3-picomatch-only.json'));
  assert.equal(v.ok, false);
  assert.match(v.problems.join('\n'), new RegExp(PICOMATCH));
});

test('listed-only (real Kit 3 audit): passes, and names every package carrying the accepted advisory', () => {
  const v = evaluateNpmAudit(fixture('kit3-picomatch-only.json'), LISTED);
  assert.equal(v.ok, true, v.problems.join('\n'));
  assert.deepEqual(v.advisories.map((a) => [a.id, a.accepted]), [[PICOMATCH, true]]);
  assert.deepEqual(v.carriers[PICOMATCH].sort(), [
    '@sveltejs/adapter-static', '@sveltejs/kit', '@sveltejs/vite-plugin-svelte', 'fdir',
    'picomatch', 'svelte-check', 'tinyglobby', 'vite', 'vitest',
  ]);
});

test('listed + unlisted High (real master audit): fails on the Kit advisories, not on picomatch', () => {
  const v = evaluateNpmAudit(fixture('kit2-master.json'), LISTED);
  assert.equal(v.ok, false);
  const failing = v.advisories.filter((a) => a.blocking && !a.accepted).map((a) => a.id).sort();
  assert.ok(failing.includes('GHSA-3r8g-jcqq-3p5j'), 'Kit High must fail');
  assert.ok(failing.includes('GHSA-6cf5-gc4q-fm3x'), 'Kit Moderate must fail');
  assert.ok(!failing.includes(PICOMATCH));
  assert.ok(v.problems.every((p) => !p.startsWith(PICOMATCH)));
  // Kit's two Low advisories are reported, never failing.
  assert.deepEqual(
    v.advisories.filter((a) => a.severity === 'low').map((a) => a.blocking),
    [false, false],
  );
});

test('an unlisted Moderate alone fails', () => {
  const v = evaluateNpmAudit(report([['picomatch', PICOMATCH, 'high'], ['left-pad', 'GHSA-aaaa-bbbb-cccc', 'moderate']]), LISTED);
  assert.equal(v.ok, false);
  assert.match(v.problems.join('\n'), /GHSA-aaaa-bbbb-cccc \(moderate, left-pad/);
});

test('an unlisted Critical fails', () => {
  const v = evaluateNpmAudit(report([['picomatch', PICOMATCH, 'high'], ['x', 'GHSA-aaaa-bbbb-dddd', 'critical']]), LISTED);
  assert.equal(v.ok, false);
});

test('an accepted ID on a different package does not pass', () => {
  const v = evaluateNpmAudit(report([['picomatch', PICOMATCH, 'high'], ['not-picomatch', PICOMATCH, 'high']]), LISTED);
  assert.equal(v.ok, false);
  assert.match(v.problems.join('\n'), /not-picomatch/);
});

test('unlisted Lows pass and stay visible', () => {
  const v = evaluateNpmAudit(report([['picomatch', PICOMATCH, 'high'], ['y', 'GHSA-aaaa-bbbb-eeee', 'low']]), LISTED);
  assert.equal(v.ok, true, v.problems.join('\n'));
  assert.deepEqual(v.advisories.find((a) => a.package === 'y'), {
    id: 'GHSA-aaaa-bbbb-eeee', package: 'y', severity: 'low', title: 'y advisory', range: '<1.0.0',
    blocking: false, accepted: false,
  });
});

test('a listed ID the audit no longer reports fails as stale', () => {
  const v = evaluateNpmAudit(report([]), LISTED);
  assert.equal(v.ok, false);
  assert.deepEqual(v.stale, [PICOMATCH]);
  assert.match(v.problems.join('\n'), /no longer reports it/);
});

test('an audit-service error fails closed', () => {
  const v = evaluateNpmAudit(JSON.stringify({ error: { code: 'ENOAUDIT', summary: 'Your configured registry does not support audit requests.' } }));
  assert.equal(v.ok, false);
  assert.match(v.problems[0], /ENOAUDIT/);
});

test('unparseable or shapeless output fails closed', () => {
  assert.equal(evaluateNpmAudit('').ok, false);
  assert.equal(evaluateNpmAudit('npm ERR! network').ok, false);
  assert.equal(evaluateNpmAudit('{}').ok, false);
});

test('an advisory with no GHSA ID or an unknown severity fails closed', () => {
  const noId = JSON.stringify({ vulnerabilities: { z: { severity: 'high', via: [{ name: 'z', url: 'https://example.test/1', severity: 'high' }] } } });
  assert.equal(evaluateNpmAudit(noId, []).ok, false);
  const odd = report([['z', 'GHSA-aaaa-bbbb-ffff', 'severe']]);
  assert.equal(evaluateNpmAudit(odd, []).ok, false);
});

test('a Moderate+ entry whose via chain reaches no advisory fails closed', () => {
  const orphan = JSON.stringify({ vulnerabilities: { a: { severity: 'high', via: ['b'] }, b: { severity: 'high', via: ['a'] } } });
  const v = evaluateNpmAudit(orphan, []);
  assert.equal(v.ok, false);
  assert.match(v.problems.join('\n'), /no advisory behind it/);
});
