#!/usr/bin/env pwsh
# ─────────────────────────────────────────────────────────────────────────────
# Fast LOCAL island dev loop.
#
# Rebuilds a Svelte island and pushes the fresh bundle straight into the running
# `familyapp-app` container's static-file dir — NO `dotnet publish`, NO
# `docker build`, NO container recreate, NO nginx restart. Seconds, not minutes,
# and it never touches container lifecycle (so it can't wedge the daemon the way
# a `docker compose up`/recreate can).
#
# The island is just static JS/CSS the app serves from
# /app/wwwroot/islands/<name>/, so copying the new dist over it + a hard-refresh
# is all that's needed to see a change.
#
#   ⚠ EPHEMERAL: the copy lives only in the running container. It is lost on a
#     container recreate, and it does NOT change the baked image. For a permanent
#     change (i.e. before a PR / deploy) do a real image build with docker-build.sh.
#
# Usage (from anywhere):
#   pwsh ./scripts/sync-island.ps1                 # syncs the chores island
#   pwsh ./scripts/sync-island.ps1 shopping-list   # syncs the shopping-list island
#   pwsh ./scripts/sync-island.ps1 chores -NoBuild # copy existing dist without rebuilding
# ─────────────────────────────────────────────────────────────────────────────
param(
    [string]$Island = "chores",
    [string]$Container = "familyapp-app",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$islandDir = Join-Path $repo "frontend/$Island"
$dist = Join-Path $islandDir "dist"

if (-not (Test-Path $islandDir)) { throw "No island at $islandDir" }

# Fail fast with a clear message if the container isn't up (e.g. daemon wedged).
$running = (docker ps --filter "name=$Container" --filter "status=running" --format "{{.Names}}")
if ($running -ne $Container) {
    throw "Container '$Container' is not running. Start the stack first (docker compose up -d), then re-run."
}

if (-not $NoBuild) {
    Write-Host "→ Building '$Island' island (vite)…" -ForegroundColor Cyan
    Push-Location $islandDir
    try { npm run build } finally { Pop-Location }
}

if (-not (Test-Path $dist)) { throw "No build output at $dist — run without -NoBuild first." }

Write-Host "→ Copying dist into ${Container}:/app/wwwroot/islands/$Island/ …" -ForegroundColor Cyan
docker cp "$dist/." "${Container}:/app/wwwroot/islands/$Island/"
if ($LASTEXITCODE -ne 0) { throw "docker cp failed." }

Write-Host ""
Write-Host "✓ '$Island' synced into the running app." -ForegroundColor Green
Write-Host "  Hard-refresh  http://localhost:8080/$Island   (Ctrl+Shift+R)" -ForegroundColor Green
