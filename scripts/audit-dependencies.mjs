#!/usr/bin/env node
// Run locally and in CI. Low findings stay visible; Moderate and above fail.
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
let failed = false;
function run(command, args, cwd = root) {
  console.log(`\n$ ${command} ${args.join(' ')}`);
  const result = spawnSync(command, args, {
    cwd, stdio: 'inherit', windowsHide: true,
    // Windows' npm entry point is a cmd shim; all arguments here are fixed literals.
    shell: process.platform === 'win32' && command === 'npm',
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
run('npm', ['audit', '--audit-level=moderate'], fileURLToPath(new URL('../frontend/app/', import.meta.url)));
console.log(`\nDependency audit: ${failed ? 'FAIL' : 'PASS'} (Moderate or higher; audit errors fail closed)`);
process.exitCode = failed ? 1 : 0;
