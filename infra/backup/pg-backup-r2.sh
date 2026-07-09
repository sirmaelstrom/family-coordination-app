#!/usr/bin/env bash
#
# pg-backup-r2.sh — nightly offsite Postgres backup for family-coordination-app.
#
# Dumps the production Postgres DB (custom format, version-matched pg_dump run
# INSIDE the container via `docker exec`; local-socket trust auth, no password),
# optionally encrypts it with age (asymmetric — the host holds only the public
# recipient; the private key lives off-host in Bitwarden Secrets), uploads it to
# a Cloudflare R2 bucket, prunes old objects, and pings a healthchecks.io
# dead-man's-switch so a silently-failed run gets noticed.
#
# Runs as the `sirm` user (member of the docker group) from a user crontab.
# Config lives in an env file (default ~/familyapp-backup/backup.env, mode 600).
# See infra/backup/README.md for install + restore.
#
set -euo pipefail

# cron runs with a minimal PATH — make brew tools (rclone, age) + docker reachable.
export PATH="/home/linuxbrew/.linuxbrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:${PATH:-}"

# --------------------------------------------------------------------------
# Config
# --------------------------------------------------------------------------
ENV_FILE="${FAMILYAPP_BACKUP_ENV:-$HOME/familyapp-backup/backup.env}"
if [ ! -r "$ENV_FILE" ]; then
  echo "[familyapp-backup] FATAL: env file not readable: $ENV_FILE" >&2
  exit 1
fi
# shellcheck disable=SC1090
set -a; . "$ENV_FILE"; set +a

PG_CONTAINER="${PG_CONTAINER:-familyapp-postgres}"
PG_USER="${PG_USER:-familyapp}"
PG_DB="${PG_DB:-familyapp}"
R2_BUCKET="${R2_BUCKET:?R2_BUCKET is required in $ENV_FILE}"
: "${R2_ACCOUNT_ID:?R2_ACCOUNT_ID is required in $ENV_FILE}"
: "${R2_ACCESS_KEY_ID:?R2_ACCESS_KEY_ID is required in $ENV_FILE}"
: "${R2_SECRET_ACCESS_KEY:?R2_SECRET_ACCESS_KEY is required in $ENV_FILE}"
RETAIN_DAILY="${RETAIN_DAILY:-14}"
RETAIN_MONTHLY="${RETAIN_MONTHLY:-6}"

WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/familyapp-backup.XXXXXX")"
trap 'rm -rf "$WORKDIR"' EXIT

# --------------------------------------------------------------------------
# Heartbeat helpers (healthchecks.io-compatible; no-op if HEALTHCHECK_URL unset)
# --------------------------------------------------------------------------
hc() {  # hc [suffix]   e.g. hc /start | hc /fail | hc (success)
  [ -n "${HEALTHCHECK_URL:-}" ] || return 0
  curl -fsS -m 10 --retry 3 -o /dev/null "${HEALTHCHECK_URL}${1:-}" || true
}
fail() { echo "[familyapp-backup] ERROR: $*" >&2; hc /fail; exit 1; }
log()  { echo "[familyapp-backup] $(date -u +%FT%TZ) $*"; }

hc /start
log "starting backup (container=$PG_CONTAINER bucket=$R2_BUCKET)"

# --------------------------------------------------------------------------
# 1. Dump — custom format, compressed. pg_dump runs inside the container so it
#    version-matches the server exactly; local-socket auth is 'trust' (pg_hba),
#    so no password is needed. Streams to the host over stdout.
# --------------------------------------------------------------------------
ts="$(date -u +%Y-%m-%dT%H%M%SZ)"   # UTC; sorts chronologically as a string
dom="$(date -u +%d)"                # day-of-month (monthly snapshot on the 1st)
base="familyapp_${ts}.dump"
dumpfile="$WORKDIR/$base"

if ! docker exec "$PG_CONTAINER" pg_dump -U "$PG_USER" -d "$PG_DB" -Fc --no-owner --no-privileges \
      > "$dumpfile" 2> "$WORKDIR/pg_dump.err"; then
  sed 's/^/[pg_dump] /' "$WORKDIR/pg_dump.err" >&2 || true
  fail "pg_dump failed"
fi

sz="$(stat -c%s "$dumpfile")"
[ "$sz" -ge 1000 ] || fail "dump suspiciously small ($sz bytes) — refusing to upload"
log "dump ok: $base ($sz bytes)"

# --------------------------------------------------------------------------
# 2. Optional encryption (asymmetric age). AGE_RECIPIENT is the PUBLIC key; the
#    private key is NOT on this host (Bitwarden Secrets FAMILYAPP_BACKUP_AGE_KEY).
# --------------------------------------------------------------------------
upload="$dumpfile"; suffix=""
if [ -n "${AGE_RECIPIENT:-}" ]; then
  age -r "$AGE_RECIPIENT" -o "$dumpfile.age" "$dumpfile" || fail "age encryption failed"
  upload="$dumpfile.age"; suffix=".age"
  log "encrypted to age recipient ${AGE_RECIPIENT:0:16}…"
fi

# --------------------------------------------------------------------------
# 3. Upload to R2 (rclone on-the-fly s3 remote from env — no rclone.conf needed)
# --------------------------------------------------------------------------
export RCLONE_CONFIG=/dev/null                 # no rclone.conf — remote is fully env-defined
export RCLONE_CONFIG_R2_TYPE=s3
export RCLONE_CONFIG_R2_PROVIDER=Cloudflare
export RCLONE_CONFIG_R2_ACCESS_KEY_ID="$R2_ACCESS_KEY_ID"
export RCLONE_CONFIG_R2_SECRET_ACCESS_KEY="$R2_SECRET_ACCESS_KEY"
export RCLONE_CONFIG_R2_ENDPOINT="https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com"
export RCLONE_CONFIG_R2_REGION=auto
export RCLONE_CONFIG_R2_NO_CHECK_BUCKET=true   # scoped token may lack bucket-create perms

obj="${base}${suffix}"
rclone copyto "$upload" "R2:${R2_BUCKET}/daily/${obj}" || fail "R2 upload (daily) failed"
log "uploaded daily/${obj}"

if [ "$dom" = "01" ]; then
  rclone copyto "$upload" "R2:${R2_BUCKET}/monthly/${obj}" || fail "R2 upload (monthly) failed"
  log "uploaded monthly/${obj}"
fi

# --------------------------------------------------------------------------
# 4. Prune — keep the newest N objects per prefix (names sort chronologically)
# --------------------------------------------------------------------------
prune() {  # prune <prefix> <keep>
  local prefix="$1" keep="$2" objs total del i
  mapfile -t objs < <(rclone lsf "R2:${R2_BUCKET}/${prefix}/" 2>/dev/null | grep -v '/$' | sort)
  total="${#objs[@]}"
  [ "$total" -gt "$keep" ] || return 0
  del=$(( total - keep ))
  for (( i=0; i<del; i++ )); do
    if rclone deletefile "R2:${R2_BUCKET}/${prefix}/${objs[$i]}"; then
      log "pruned ${prefix}/${objs[$i]}"
    else
      echo "[familyapp-backup] WARN: prune failed for ${prefix}/${objs[$i]}" >&2
    fi
  done
}
prune daily   "$RETAIN_DAILY"
prune monthly "$RETAIN_MONTHLY"

log "backup complete"
hc   # success ping
