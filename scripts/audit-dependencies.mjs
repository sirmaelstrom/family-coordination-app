#!/usr/bin/env node
// Run locally and in CI. Low findings stay visible; Moderate and above fail, except the exact
// GHSA IDs accepted in scripts/npm-audit-filter.mjs (each documented in docs/dependency-security.md).
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { ACCEPTED_ADVISORIES, evaluateNpmAudit } from './npm-audit-filter.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
let failed = false;
function run(command, args, cwd = root) {
  console.log(`\n$ ${command} ${args.join(' ')}`);
  const result = spawnSync(command, args, {
    cwd, stdio: 'inherit', windowsHide: true,
  });
  if (result.error) console.error(result.error.message);
  if (result.status !== 0) failed = true;
}

for (const project of [
  'src/FamilyCoordinationApp/FamilyCoordinationApp.csproj',
  'tests/FamilyCoordinationApp.Tests/FamilyCoordinationApp.Tests.csproj',
]) {
  run('dotnet', ['restore', project, '--force-evaluate', '--no-http-cache',
    '-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-p:NuGetAuditLevel=low',
    '-warnaserror:NU1900,NU1902,NU1903,NU1904,NU1905']);
}

// npm: read the JSON report and decide here, so an accepted advisory can pass while any other
// Moderate+ still fails. npm audit exits nonzero whenever it finds anything, so its exit code is
// not the verdict; an unreadable report or an `error` object fails closed in evaluateNpmAudit.
console.log('\n$ npm audit --json');
// One fixed command string through the shell: Windows' npm entry point is a cmd shim, and a
// string (not an args array) avoids Node's DEP0190 warning about unescaped shell arguments.
const npm = spawnSync('npm audit --json', {
  cwd: fileURLToPath(new URL('../frontend/app/', import.meta.url)),
  encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, windowsHide: true, shell: true,
});
if (npm.error) console.error(npm.error.message);
if (npm.stderr) process.stderr.write(npm.stderr);
const verdict = evaluateNpmAudit(npm.stdout ?? '');

for (const a of verdict.advisories) {
  const tag = a.accepted ? 'ACCEPTED' : a.blocking ? 'FAIL' : 'low (reported)';
  console.log(`  [${tag}] ${a.id} ${a.severity} ${a.package} ${a.range} - ${a.title}`);
  if (a.accepted) {
    const via = (verdict.carriers[a.id] ?? []).filter((p) => p !== a.package);
    const why = ACCEPTED_ADVISORIES.find((x) => x.id === a.id)?.why;
    console.log(`      carried by ${verdict.carriers[a.id]?.length ?? 0} entries: ${a.package}${via.length ? ` <- ${via.join(', ')}` : ''}`);
    if (why) console.log(`      accepted: ${why}`);
  }
}
if (verdict.advisories.length === 0 && verdict.ok) console.log('  no npm advisories');
for (const p of verdict.problems) console.error(`  npm audit problem: ${p}`);
if (!verdict.ok) failed = true;

console.log(`\nDependency audit: ${failed ? 'FAIL' : 'PASS'} (Moderate or higher; accepted: ${ACCEPTED_ADVISORIES.map((a) => a.id).join(', ') || 'none'}; audit errors fail closed)`);
process.exitCode = failed ? 1 : 0;
