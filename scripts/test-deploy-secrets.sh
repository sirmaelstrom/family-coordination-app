#!/bin/bash
# test-deploy-secrets.sh — exercises deploy.sh's secret-writing block against a fixture SECRETS_JSON.
# Extracts deploy.sh's real log/die helpers and the block from the get_secret helper up to the
# "Secrets generated" log line, runs it in a scratch dir, and checks exit status, .env and the
# postgres secret file. A missing secret must stop the deploy and leave the previous .env alone.
# Usage: scripts/test-deploy-secrets.sh [path/to/deploy.sh]   (default: ../deploy.sh)

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_SH="${1:-$HERE/../deploy.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

HELPERS="$(grep -E '^(log|die)\(\) ' "$DEPLOY_SH")"
[[ $(grep -c . <<<"$HELPERS") == 2 ]] || { echo "FAIL: could not extract log/die from $DEPLOY_SH"; exit 2; }
BLOCK="$(sed -n '/^# Helper to extract a secret value by key/,/^log "Secrets generated/p' "$DEPLOY_SH" | sed '$d')"
[[ -n "$BLOCK" ]] || { echo "FAIL: could not extract the secrets block from $DEPLOY_SH"; exit 2; }

ALL_KEYS=(GOOGLE_CLIENT_ID GOOGLE_CLIENT_SECRET DATAPROTECTION_CERT GEMINI_API_KEY POSTGRES_PASSWORD_PROD)

secrets_json() { # keys to leave out...
  local k skip out="" sep=""
  for k in "${ALL_KEYS[@]}"; do
    for skip in "$@"; do [[ "$k" == "$skip" ]] && continue 2; done
    out+="$sep{\"key\":\"$k\",\"value\":\"val-$k\"}"; sep=","
  done
  echo "[$out]"
}

PASS=0
FAILS=0
check() { # description, condition-result (0 = ok)
  if [[ "$2" == 0 ]]; then PASS=$((PASS + 1)); echo "  ok   - $1"; else FAILS=$((FAILS + 1)); echo "  FAIL - $1"; fi
}

run_case() { # name, keys to leave out...
  local name="$1" dir="$WORK/$1"; shift
  mkdir -p "$dir"
  printf 'TEMPLATE=1\n' > "$dir/.env.local"
  printf 'PREVIOUS=1\n' > "$dir/.env"
  local json; json="$(secrets_json "$@")"
  (
    cd "$dir"
    set -euo pipefail
    LOG="$dir/log"
    SECRETS_JSON="$json"
    # A native Windows jq writes CRLF unless told otherwise; Linux jq needs nothing.
    case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) jq() { command jq -b "$@"; } ;; esac
    eval "$HELPERS"
    eval "$BLOCK"
    echo REACHED >> "$dir/reached"
  ) > "$dir/stdout" 2> "$dir/stderr"
  echo $? > "$dir/rc"
}

rc() { cat "$WORK/$1/rc"; }
reached() { [[ -f "$WORK/$1/reached" ]]; }
env_is_previous() { [[ "$(cat "$WORK/$1/.env")" == "PREVIOUS=1" ]]; }

echo "(a) every secret present"
run_case a
check "proceeds (rc $(rc a), reached code after the block)" "$([[ "$(rc a)" == 0 ]] && reached a; echo $?)"
expected_env="$(printf 'TEMPLATE=1\n\nGOOGLE_CLIENT_ID=val-GOOGLE_CLIENT_ID\nGOOGLE_CLIENT_SECRET=val-GOOGLE_CLIENT_SECRET\nDATAPROTECTION_CERT=val-DATAPROTECTION_CERT\nGEMINI_API_KEY=val-GEMINI_API_KEY')"
check ".env is the template plus the four secrets" "$([[ "$(cat "$WORK/a/.env")" == "$expected_env" ]]; echo $?)"
check "postgres secret file holds the password and a newline" "$([[ "$(od -An -c "$WORK/a/secrets/postgres_password" | tr -s ' ')" == "$(printf 'val-POSTGRES_PASSWORD_PROD\n' | od -An -c | tr -s ' ')" ]]; echo $?)"
check "no secret value in stdout, stderr or log" "$(! cat "$WORK/a/stdout" "$WORK/a/stderr" "$WORK/a/log" 2>/dev/null | grep -q 'val-'; echo $?)"

for missing in GOOGLE_CLIENT_ID GEMINI_API_KEY POSTGRES_PASSWORD_PROD; do
  echo "(missing $missing)"
  run_case "m-$missing" "$missing"
  check "dies (rc $(rc "m-$missing"), never reached code after the block)" "$([[ "$(rc "m-$missing")" == 1 ]] && ! reached "m-$missing"; echo $?)"
  check "previous .env left untouched" "$(env_is_previous "m-$missing"; echo $?)"
  check "no postgres secret file written" "$([[ ! -e "$WORK/m-$missing/secrets/postgres_password" ]]; echo $?)"
  check "error names the key on stderr" "$(grep -q "ERROR: Secret '$missing' not found in BWS" "$WORK/m-$missing/stderr"; echo $?)"
  check "error is in the deploy log" "$(grep -q "ERROR: Secret '$missing' not found in BWS" "$WORK/m-$missing/log"; echo $?)"
done

echo "$PASS passed, $FAILS failed"
[[ "$FAILS" == 0 ]]
