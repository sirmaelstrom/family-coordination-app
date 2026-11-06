#!/usr/bin/env bash
# Isolated local staging stack for the agent endpoint (quest 8bfa42cf). Builds an image from a checkout and runs it
# on a scratch compose project (own containers, network, Postgres volume) bound to 127.0.0.1 only, with /mcp on and
# the Development auth bypass as the signed-in member. Never touches the dev stack (familyapp-*) or production, and
# publishes nothing beyond loopback.
#
#   scripts/mcp-staging.sh up [port]           build this checkout's HEAD, start, seed, enable /mcp (default port 8095)
#   scripts/mcp-staging.sh as <email>          restart the app signed in as another seeded member (empty: first user)
#   scripts/mcp-staging.sh psql "<sql>"        run SQL in the stack's Postgres
#   scripts/mcp-staging.sh down                remove containers, volume and image
#
# State (port, image, throwaway Postgres password) lives in $MCP_STAGING_DIR (default: a temp directory).
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
DIR="${MCP_STAGING_DIR:-${TMPDIR:-/tmp}/fca-mcp-staging}"
PROJECT="${MCP_STAGING_PROJECT:-familyapp-mcp}"
mkdir -p "$DIR"
STATE="$DIR/state.env"
[ -f "$STATE" ] && . "$STATE"

overlay() {
  cat > "$DIR/override.yml" <<'YAML'
services:
  app:
    environment:
      Mcp__Enabled: "true"
      DevAuth__Email: ${DEVAUTH_EMAIL:-}
YAML
}

compose() {
  FAMILYAPP_IMAGE="$IMAGE" VERIFY_POSTGRES_SECRET_FILE="$DIR/pg-secret" VERIFY_APP_PORT="$PORT" \
    docker compose -p "$PROJECT" -f "$REPO/docker-compose.verify.yml" -f "$DIR/override.yml" "$@"
}

wait_up() {
  for _ in $(seq 1 90); do
    curl -s -o /dev/null "http://127.0.0.1:$PORT/health" && return 0
    sleep 2
  done
  echo "app did not answer on 127.0.0.1:$PORT" >&2
  return 1
}

case "${1:-}" in
  up)
    PORT="${2:-8095}"
    IMAGE="familyapp:mcp-$(git -C "$REPO" rev-parse --short=12 HEAD)"
    [ -f "$DIR/pg-secret" ] || head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' > "$DIR/pg-secret"
    printf 'PORT=%q\nIMAGE=%q\n' "$PORT" "$IMAGE" > "$STATE"
    overlay
    docker build -q -t "$IMAGE" "$REPO"
    DEVAUTH_EMAIL="" compose up -d
    wait_up
    docker exec -i "$PROJECT-postgres-1" psql -q -U familyapp -d familyapp -v ON_ERROR_STOP=1 < "$REPO/docker/verify/seed.sql"
    DEVAUTH_EMAIL="" compose restart app
    wait_up
    echo "up: $IMAGE at http://127.0.0.1:$PORT (project $PROJECT; /mcp enabled)"
    ;;
  as)
    DEVAUTH_EMAIL="${2:-}" compose up -d --force-recreate app
    wait_up
    echo "signed in as ${2:-<first user>}"
    ;;
  psql)
    docker exec -i "$PROJECT-postgres-1" psql -U familyapp -d familyapp -v ON_ERROR_STOP=1 -c "$2"
    ;;
  down)
    DEVAUTH_EMAIL="" compose down -v
    docker image rm "$IMAGE" || true
    ;;
  *)
    echo "usage: $0 up [port] | as <email> | psql <sql> | down" >&2
    exit 2
    ;;
esac
