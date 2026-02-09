#!/bin/bash
# deploy.sh — Family Coordination App deployment
# Pulls secrets from a secrets manager, builds (optional), and deploys.
#
# Usage:
#   ./deploy.sh              — Regenerate secrets + recreate containers
#   ./deploy.sh --build      — Rebuild Docker image + deploy
#   ./deploy.sh --no-pull    — Skip git pull (for CI which already pulled)
#   ./deploy.sh --restart    — Restart containers only (no secret regen)
#   ./deploy.sh down         — Stop all containers
#
# Flags can be combined: ./deploy.sh --build --no-pull

set -euo pipefail

# ── Config ──────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

LOG_DIR="$HOME/data/logs"
LOG="$LOG_DIR/familyapp-deploy.log"
COMPOSE="docker compose -f docker-compose.yml -f docker-compose.prod.yml"
BWS="$HOME/.local/bin/bws"
BWS_TOKEN_FILE="$HOME/.bws-token"

# ── Logging ─────────────────────────────────────────────────────────
mkdir -p "$LOG_DIR"
log() { echo "[$(date -Iseconds)] $*" | tee -a "$LOG"; }
die() { log "ERROR: $*"; exit 1; }

# ── Parse flags ─────────────────────────────────────────────────────
DO_BUILD=false
DO_PULL=true
DO_RESTART_ONLY=false
DO_DOWN=false

for arg in "$@"; do
  case "$arg" in
    --build)    DO_BUILD=true ;;
    --no-pull)  DO_PULL=false ;;
    --restart)  DO_RESTART_ONLY=true ;;
    down)       DO_DOWN=true ;;
    *)          die "Unknown argument: $arg" ;;
  esac
done

log "=== Deploy started (build=$DO_BUILD pull=$DO_PULL restart_only=$DO_RESTART_ONLY down=$DO_DOWN) ==="

# ── Stop ────────────────────────────────────────────────────────────
if $DO_DOWN; then
  log "Stopping containers..."
  $COMPOSE down --remove-orphans
  log "Containers stopped."
  exit 0
fi

# ── Restart only ────────────────────────────────────────────────────
if $DO_RESTART_ONLY; then
  log "Restarting containers..."
  $COMPOSE restart
  log "Containers restarted."
  exit 0
fi

# ── Prerequisites ───────────────────────────────────────────────────
command -v docker >/dev/null 2>&1 || die "docker not found"
command -v jq >/dev/null 2>&1     || die "jq not found"
[[ -x "$BWS" ]]                   || die "BWS CLI not found at $BWS"
[[ -f "$BWS_TOKEN_FILE" ]]        || die "BWS token not found at $BWS_TOKEN_FILE"

# ── Git pull ────────────────────────────────────────────────────────
if $DO_PULL; then
  log "Pulling latest code..."
  BRANCH=$(git rev-parse --abbrev-ref HEAD)
  git fetch origin
  git reset --hard "origin/$BRANCH"
  log "Pulled commit: $(git rev-parse --short HEAD) on $BRANCH"
fi

# ── Generate secrets from BWS ───────────────────────────────────────
log "Fetching secrets from Bitwarden..."
export BWS_ACCESS_TOKEN
BWS_ACCESS_TOKEN=$(cat "$BWS_TOKEN_FILE")

SECRETS_JSON=$("$BWS" secret list --output json) || die "BWS secret fetch failed"

# Helper to extract a secret value by key
get_secret() {
  local key="$1"
  local val
  val=$(echo "$SECRETS_JSON" | jq -r --arg k "$key" '.[] | select(.key == $k) | .value')
  [[ -n "$val" && "$val" != "null" ]] || die "Secret '$key' not found in BWS"
  echo "$val"
}

# Start .env from non-secret template
[[ -f .env.local ]] || die ".env.local template not found"
cp .env.local .env
echo "" >> .env

# Append BWS secrets to .env
echo "GOOGLE_CLIENT_ID=$(get_secret GOOGLE_CLIENT_ID)" >> .env
echo "GOOGLE_CLIENT_SECRET=$(get_secret GOOGLE_CLIENT_SECRET)" >> .env
echo "DATAPROTECTION_CERT=$(get_secret DATAPROTECTION_CERT)" >> .env
chmod 600 .env

# Write Postgres password as Docker secret file
mkdir -p secrets
get_secret POSTGRES_PASSWORD_PROD > secrets/postgres_password
chmod 600 secrets/postgres_password

log "Secrets generated (.env + secrets/postgres_password)"

# ── Build ───────────────────────────────────────────────────────────
if $DO_BUILD; then
  log "Building Docker image..."
  docker build -t familyapp:latest . 2>&1 | tail -5 | tee -a "$LOG"
  log "Image built: familyapp:latest"
fi

# ── Deploy ──────────────────────────────────────────────────────────
log "Deploying containers..."

# Clean up any stale containers
docker rm -f familyapp-app familyapp-nginx familyapp-postgres 2>/dev/null || true
$COMPOSE down --remove-orphans 2>/dev/null || true
docker network prune -f 2>/dev/null || true
sleep 2

$COMPOSE up -d --force-recreate
log "Containers started"

# ── Health check ────────────────────────────────────────────────────
log "Waiting for health check..."
for i in {1..30}; do
  STATUS=$(docker inspect familyapp-app --format='{{.State.Health.Status}}' 2>/dev/null || echo "starting")
  if [[ "$STATUS" == "healthy" ]]; then
    log "App is healthy!"
    break
  fi
  if [[ $i -eq 30 ]]; then
    log "Health check timeout after 60s"
    docker logs familyapp-app --tail 30 >> "$LOG" 2>&1
    die "Health check failed"
  fi
  echo "  Status: $STATUS ($i/30)"
  sleep 2
done

# ── Cleanup ─────────────────────────────────────────────────────────
docker image prune -f --filter "until=24h" >/dev/null 2>&1
log "=== Deploy complete ==="
