# deploy-from-windows.ps1 — Deploy family-coordination-app to darktower from Windows
# Usage:
#   .\deploy-from-windows.ps1              — Regenerate secrets + recreate containers
#   .\deploy-from-windows.ps1 --build      — Rebuild Docker image + deploy
#   .\deploy-from-windows.ps1 --restart    — Restart containers only
#   .\deploy-from-windows.ps1 down         — Stop all containers
#   .\deploy-from-windows.ps1 --status     — Check container status

param(
    [switch]$Build,
    [switch]$Restart,
    [switch]$Status,
    [switch]$Down
)

$ErrorActionPreference = "Stop"
$DarktowerHost = "thedarktower"
$RemotePath = "~/familyapp"

Write-Host "=== Family Coordination App — Remote Deploy ===" -ForegroundColor Cyan

if ($Status) {
    Write-Host "Checking container status on darktower..."
    ssh $DarktowerHost "docker ps --filter name=familyapp --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'"
    exit 0
}

if ($Down) {
    Write-Host "Stopping containers on darktower..."
    ssh $DarktowerHost "cd $RemotePath && ./deploy.sh down"
    exit 0
}

$flags = @()
if ($Build)   { $flags += "--build" }
if ($Restart) { $flags += "--restart" }

$flagStr = ($flags -join " ")
if ($flagStr) {
    Write-Host "Running: deploy.sh $flagStr"
    ssh $DarktowerHost "cd $RemotePath && ./deploy.sh $flagStr"
} else {
    Write-Host "Running: deploy.sh (full deploy)"
    ssh $DarktowerHost "cd $RemotePath && ./deploy.sh"
}

Write-Host ""
Write-Host "=== Deploy complete ===" -ForegroundColor Green
