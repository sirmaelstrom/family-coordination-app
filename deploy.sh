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

# ── Concurrency lock ────────────────────────────────────────────────
# Prevent overlapping deploys (CI rapid-fire commits cause race conditions)
LOCK_FILE="/tmp/familyapp-deploy.lock"
exec 200>"$LOCK_FILE"
if ! flock -n 200; then
  echo "Another deploy is already running — waiting up to 120s..."
  flock -w 120 200 || { echo "ERROR: Timed out waiting for deploy lock"; exit 1; }
fi

# ── Config ──────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

LOG_DIR="$HOME/data/logs"
LOG="$LOG_DIR/familyapp-deploy.log"
COMPOSE="sudo docker compose -f docker-compose.yml -f docker-compose.prod.yml"
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
echo "GEMINI_API_KEY=$(get_secret GEMINI_API_KEY)" >> .env
chmod 600 .env

# Write Postgres password as Docker secret file
mkdir -p secrets
get_secret POSTGRES_PASSWORD_PROD > secrets/postgres_password
chmod 600 secrets/postgres_password

log "Secrets generated (.env + secrets/postgres_password)"

# ── Build ───────────────────────────────────────────────────────────
if $DO_BUILD; then
  log "Building Docker image..."
  sudo docker build -t familyapp:latest . 2>&1 | tail -5 | tee -a "$LOG"
  log "Image built: familyapp:latest"
fi

# ── DB safety guard + pre-deploy backup ─────────────────────────────
# Guards the "silent fresh empty DB" failure mode. The deploy below tears down
# and force-recreates postgres; prod's data lives in the bind mount
# ${POSTGRES_DATA_PATH}. If that path is ever unset/empty/uninitialized,
# postgres runs initdb and starts a BRAND-NEW cluster with NO error — orphaning
# production data. We (1) require the var, (2) snapshot the live DB before any
# teardown, and (3) refuse to start on an uninitialized data dir. The data dir
# is root-owned, so existence checks use `sudo test`. (See DEPLOY-SAFETY.md.)
DB_DATA_PATH=$(grep -E '^[[:space:]]*POSTGRES_DATA_PATH=' .env | tail -n1 | cut -d= -f2- | tr -d '"' | xargs || true)
[[ -n "$DB_DATA_PATH" ]] || die "POSTGRES_DATA_PATH is unset in .env — refusing to deploy (postgres would create a FRESH empty DB and orphan production data). Set it in .env.local."

# (2) Back up the currently-running DB before recreating containers.
BACKUP_DIR="$HOME/data/db-backups"
mkdir -p "$BACKUP_DIR"
if sudo docker ps --format '{{.Names}}' | grep -qx 'familyapp-postgres'; then
  STAMP=$(date +%Y%m%d-%H%M%S)
  BACKUP_FILE="$BACKUP_DIR/familyapp-$STAMP.sql.gz"
  log "Backing up live database before deploy → $BACKUP_FILE"
  if sudo docker exec familyapp-postgres sh -c 'PGPASSWORD=$(cat /run/secrets/postgres_password) pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB"' 2>>"$LOG" | gzip > "$BACKUP_FILE" && [[ -s "$BACKUP_FILE" ]]; then
    log "Backup OK ($(du -h "$BACKUP_FILE" | cut -f1)); pruning backups older than 14 days"
    find "$BACKUP_DIR" -name 'familyapp-*.sql.gz' -mtime +14 -delete 2>/dev/null || true
  else
    rm -f "$BACKUP_FILE"
    [[ "${ALLOW_NO_BACKUP:-0}" == "1" ]] || die "Pre-deploy DB backup FAILED — aborting to protect data. Re-run with ALLOW_NO_BACKUP=1 to override."
    log "WARNING: backup failed but ALLOW_NO_BACKUP=1 set — continuing without a fresh backup."
  fi
else
  log "No running familyapp-postgres found — skipping pre-deploy backup (first deploy on this host?)."
fi

# (3) Refuse to start on an uninitialized data dir (would trigger a fresh initdb).
# The host data dir is root-owned, so we can't `sudo test` it: in non-interactive runs (the CI
# self-hosted deploy, or `ssh host ./deploy.sh`) sudo has no TTY to read a password, and only the
# specific `sudo docker …` invocations this script uses are NOPASSWD — `sudo test` is NOT, so it
# fails and the old guard silently "passed". Instead probe the STILL-RUNNING old postgres (same
# bind mount the new container reuses) via `sudo docker exec` (passwordless, mirrors the backup
# above) and check PGDATA/PG_VERSION inside it.
if sudo docker ps --format '{{.Names}}' | grep -qx 'familyapp-postgres'; then
  if sudo docker exec familyapp-postgres sh -c 'test -f "$PGDATA/PG_VERSION"'; then
    log "DB safety check passed (live cluster initialized; data dir: $DB_DATA_PATH)"
  else
    [[ "${ALLOW_FRESH_DB:-0}" == "1" ]] || die "POSTGRES_DATA_PATH ($DB_DATA_PATH) is mounted but has no PG_VERSION — postgres would create a FRESH empty DB and orphan production data. Refusing. If this is a brand-new install, re-run with ALLOW_FRESH_DB=1."
    log "WARNING: data dir uninitialized but ALLOW_FRESH_DB=1 set — a fresh DB will be created."
  fi
else
  # No running familyapp-postgres to probe (first deploy on this host, or after `down`); the
  # pre-deploy backup was skipped for the same reason. Treat as a fresh install — postgres will
  # initdb a new cluster on first start (matches the original "path doesn't exist yet" behavior).
  log "DB safety check: no running familyapp-postgres to probe — assuming fresh install (postgres will initdb)."
fi

# ── Deploy ──────────────────────────────────────────────────────────
log "Deploying containers..."

# Clean up any stale containers
sudo docker rm -f familyapp-app familyapp-nginx familyapp-postgres 2>/dev/null || true
$COMPOSE down --remove-orphans 2>/dev/null || true
sudo docker network prune -f 2>/dev/null || true
sleep 2

$COMPOSE up -d --force-recreate
log "Containers started"

# ── Health check ────────────────────────────────────────────────────
log "Waiting for health check..."
for i in {1..30}; do
  STATUS=$(sudo docker inspect familyapp-app --format='{{.State.Health.Status}}' 2>/dev/null || echo "starting")
  if [[ "$STATUS" == "healthy" ]]; then
    log "App is healthy!"
    break
  fi
  if [[ $i -eq 30 ]]; then
    log "Health check timeout after 60s"
    sudo docker logs familyapp-app --tail 30 >> "$LOG" 2>&1
    die "Health check failed"
  fi
  echo "  Status: $STATUS ($i/30)"
  sleep 2
done

# ── Cleanup ─────────────────────────────────────────────────────────
sudo docker image prune -f --filter "until=24h" >/dev/null 2>&1
log "=== Deploy complete ==="
