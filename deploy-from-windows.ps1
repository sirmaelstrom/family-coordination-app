# deploy-from-windows.ps1 — Deploy family-coordination-app to the production host from Windows.
#
# Configure the target via environment variables (keeps host details out of the repo):
#   $env:FAMILYAPP_DEPLOY_HOST = "your-ssh-host"   # an SSH Host alias from ~/.ssh/config, or user@host
#   $env:FAMILYAPP_REMOTE_PATH = "~/familyapp"     # optional; defaults to ~/familyapp
#
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

$DeployHost = $env:FAMILYAPP_DEPLOY_HOST
if (-not $DeployHost) {
    Write-Error "Set `$env:FAMILYAPP_DEPLOY_HOST to your SSH host (an alias in ~/.ssh/config, or user@host)."
    exit 1
}
$RemotePath = if ($env:FAMILYAPP_REMOTE_PATH) { $env:FAMILYAPP_REMOTE_PATH } else { "~/familyapp" }

Write-Host "=== Family Coordination App — Remote Deploy ===" -ForegroundColor Cyan

if ($Status) {
    Write-Host "Checking container status on the deploy host..."
    ssh $DeployHost "docker ps --filter name=familyapp --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'"
    exit 0
}

if ($Down) {
    Write-Host "Stopping containers on the deploy host..."
    ssh $DeployHost "cd $RemotePath && ./deploy.sh down"
    exit 0
}

$flags = @()
if ($Build)   { $flags += "--build" }
if ($Restart) { $flags += "--restart" }

$flagStr = ($flags -join " ")
if ($flagStr) {
    Write-Host "Running: deploy.sh $flagStr"
    ssh $DeployHost "cd $RemotePath && ./deploy.sh $flagStr"
} else {
    Write-Host "Running: deploy.sh (full deploy)"
    ssh $DeployHost "cd $RemotePath && ./deploy.sh"
}

Write-Host ""
Write-Host "=== Deploy complete ===" -ForegroundColor Green
