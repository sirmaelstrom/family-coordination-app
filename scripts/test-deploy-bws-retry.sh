#!/bin/bash
# test-deploy-bws-retry.sh — exercises deploy.sh's BWS secret fetch against a fake `bws`.
# Extracts the "Generate secrets from BWS" block from deploy.sh (up to the get_secret helper),
# runs it with stub log/die/sleep, and checks call counts, sleeps, exit status and log content.
# Usage: scripts/test-deploy-bws-retry.sh [path/to/deploy.sh]   (default: ../deploy.sh)
# Not run by CI. `sleep` is stubbed, so the production schedule (5s, 15s) runs in milliseconds.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_SH="${1:-$HERE/../deploy.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

BLOCK="$(sed -n '/^log "Fetching secrets from Bitwarden/,/^# Helper to extract a secret value by key/p' "$DEPLOY_SH" | sed '$d')"
[[ -n "$BLOCK" ]] || { echo "FAIL: could not extract the fetch block from $DEPLOY_SH"; exit 2; }

# Fake bws: fails the first $FAKE_FAIL_CALLS calls with a 503 on stderr, then prints a secrets JSON.
cat > "$WORK/bws" <<'FAKE'
#!/bin/bash
n=$(( $(cat "$FAKE_DIR/calls" 2>/dev/null || echo 0) + 1 ))
echo "$n" > "$FAKE_DIR/calls"
if (( n <= FAKE_FAIL_CALLS )); then
  echo "Error: [503 Service Unavailable] upstream connect error (call $n)" >&2
  exit 1
fi
echo '[{"key":"MARKER","value":"s3cr3t-value"}]'
FAKE
chmod +x "$WORK/bws"
echo token > "$WORK/token"

PASS=0
FAILS=0
check() { # description, condition-result (0 = ok)
  if [[ "$2" == 0 ]]; then PASS=$((PASS + 1)); echo "  ok   - $1"; else FAILS=$((FAILS + 1)); echo "  FAIL - $1"; fi
}

run_case() { # name, fail_calls
  local name="$1" fail_calls="$2" dir="$WORK/$1"
  mkdir -p "$dir"
  : > "$dir/log"; : > "$dir/sleeps"; echo 0 > "$dir/calls"
  (
    export FAKE_DIR="$dir" FAKE_FAIL_CALLS="$fail_calls"
    set -euo pipefail
    BWS="$WORK/bws"; BWS_TOKEN_FILE="$WORK/token"
    log() { echo "$*" >> "$dir/log"; }
    die() { log "ERROR: $*"; exit 1; }
    sleep() { echo "$1" >> "$dir/sleeps"; }
    eval "$BLOCK"
    echo REACHED >> "$dir/log"
  ) > "$dir/stdout" 2>&1
  echo $? > "$dir/rc"
}

calls() { cat "$WORK/$1/calls"; }
rc() { cat "$WORK/$1/rc"; }
sleeps() { tr '\n' ' ' < "$WORK/$1/sleeps"; }

echo "(a) fails twice, then succeeds"
run_case a 2
check "proceeds (rc 0, reached code after fetch)" "$([[ "$(rc a)" == 0 ]] && grep -q REACHED "$WORK/a/log"; echo $?)"
check "3 bws calls (got $(calls a))" "$([[ "$(calls a)" == 3 ]]; echo $?)"
check "backoff 5 then 15 (got: $(sleeps a))" "$([[ "$(sleeps a)" == "5 15 " ]]; echo $?)"
check "each failed attempt logged with number and stderr" "$(grep -q 'attempt 1 failed: Error: \[503' "$WORK/a/log" && grep -q 'attempt 2 failed: Error: \[503' "$WORK/a/log"; echo $?)"

echo "(b) always fails"
run_case b 99
check "dies (rc 1, never reached code after fetch)" "$([[ "$(rc b)" == 1 ]] && ! grep -q REACHED "$WORK/b/log"; echo $?)"
check "exactly 3 bws calls (got $(calls b))" "$([[ "$(calls b)" == 3 ]]; echo $?)"
check "2 sleeps, none after the last attempt (got: $(sleeps b))" "$([[ "$(sleeps b)" == "5 15 " ]]; echo $?)"
check "die carries the LAST error (call 3)" "$(grep -q 'ERROR: BWS secret fetch failed after 3 attempts: Error: \[503 Service Unavailable\] upstream connect error (call 3)' "$WORK/b/log"; echo $?)"

echo "(c) succeeds first time"
run_case c 0
check "proceeds" "$([[ "$(rc c)" == 0 ]] && grep -q REACHED "$WORK/c/log"; echo $?)"
check "1 bws call (got $(calls c))" "$([[ "$(calls c)" == 1 ]]; echo $?)"
check "no sleep (got: '$(sleeps c)')" "$([[ -z "$(sleeps c)" ]]; echo $?)"

echo "(all) secret value never reaches a log line or stdout"
leak=0
for c in a b c; do grep -q 's3cr3t-value' "$WORK/$c/log" "$WORK/$c/stdout" && leak=1; done
check "no s3cr3t-value in any log/stdout" "$leak"

echo "$PASS passed, $FAILS failed"
[[ "$FAILS" == 0 ]]
