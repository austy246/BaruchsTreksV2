#!/usr/bin/env bash
# Behaviour test of deploy/remote-deploy.sh against a fake `docker` (no real Docker, VPS or registry is touched).
# Run: bash deploy/tests/test-remote-deploy.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/../remote-deploy.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail=0
ok()   { echo "ok   - $1"; }
bad()  { echo "FAIL - $1"; fail=1; }
check() { local desc="$1"; shift; if "$@"; then ok "$desc"; else bad "$desc"; fi; }
has_line() { grep -Fxq -- "$2" "$1"; }
calls() { grep -c -- "$2" "$1" || true; }

REPO=ghcr.io/austy246/baruchstreks-app
OLD="$REPO:oldsha"
NEW="$REPO:newsha"

# --- fake docker -----------------------------------------------------------------------------------------------
bin="$work/bin"; mkdir -p "$bin"
cat > "$bin/docker" <<'FAKE'
#!/usr/bin/env bash
# Records every call (one line) in $FAKE_LOG. Behaviour is steered by FAKE_* variables.
echo "$*" >> "$FAKE_LOG"
case "$1" in
  pull) exit 0 ;;
  run) [ "${FAKE_SELFTEST_FAIL:-0}" = 1 ] && exit 1; exit 0 ;;
  compose)
    sub="$2"; [ "$2" = "-p" ] && sub="$4"
    case "$sub" in
      pull) [ "${FAKE_PULL_FAIL:-0}" = 1 ] && exit 1; echo "pull env APP_IMAGE=${APP_IMAGE:-} APP_DOMAIN=${APP_DOMAIN:-}" >> "$FAKE_LOG"; exit 0 ;;
      ps) exit 0 ;;
      up)
        # "healthy" unless the image in .env is the one that must fail
        img="$(grep -m1 '^APP_IMAGE=' .env | cut -d= -f2-)"
        echo "up with $img" >> "$FAKE_LOG"
        echo "up env APP_DOMAIN=${APP_DOMAIN:-}" >> "$FAKE_LOG"
        [ -n "${FAKE_BAD_IMAGE:-}" ] && [ "$img" = "$FAKE_BAD_IMAGE" ] && exit 1
        exit 0 ;;
    esac ;;
  ps) echo cafe1234; exit 0 ;;
  exec)
    case "$3" in
      caddy)
        case "$4" in
          validate) [ "${FAKE_VALIDATE_FAIL:-0}" = 1 ] && exit 1; exit 0 ;;
          reload) exit 0 ;;
        esac ;;
    esac ;;
  images) printf '%s\n' $FAKE_IMAGES; exit 0 ;;
  rmi) exit 0 ;;
esac
exit 0
FAKE
chmod +x "$bin/docker"

# --- a fresh fake server ----------------------------------------------------------------------------------------
new_server() {
  rm -rf "$work/srv"; mkdir -p "$work/srv/deploy" "$work/sites"
  cp "$here/../baruchstreks.caddy" "$work/srv/deploy/baruchstreks.caddy"
  cat > "$work/srv/deploy/.env" <<EOF2
COMPOSE_PROJECT_NAME=baruchstreks
APP_DOMAIN=treks.example.test
ConnectionStrings__Storage=topsecret-conn
APP_IMAGE=$OLD
EOF2
  : > "$work/docker.log"
  rm -f "$work/sites/baruchstreks.caddy"*
}
deploy() { # args passed to remote-deploy.sh; stdout+stderr in $work/out.txt
  PATH="$bin:$PATH" FAKE_LOG="$work/docker.log" APP_DIR="$work/srv" SITES="$work/sites" \
    FAKE_IMAGES="$FAKE_IMAGES_VALUE" bash -s -- "$@" < "$script" > "$work/out.txt" 2>&1
}
FAKE_IMAGES_VALUE="$REPO:oldsha $REPO:ancient $REPO:newsha $REPO:<none>"
env_value() { grep -m1 "^$1=" "$work/srv/deploy/.env" | cut -d= -f2-; }

# 1. Successful deploy ------------------------------------------------------------------------------------------
new_server
deploy "$NEW"
check "success: exit 0" test $? -eq 0
check "success: APP_IMAGE set to the new image" test "$(env_value APP_IMAGE)" = "$NEW"
check "success: other .env lines untouched" test "$(env_value ConnectionStrings__Storage)" = "topsecret-conn"
check "success: previous image remembered" test "$(cat "$work/srv/deploy/.previous-image")" = "$OLD"
check "success: compose pull + up --wait" bash -c 'grep -q "^compose -p baruchstreks pull app" "$1" && grep -q "^compose -p baruchstreks up -d --wait" "$1"' _ "$work/docker.log"
check "success: EVERY compose call pins -p baruchstreks" bash -c '! grep "^compose" "$1" | grep -qv "^compose -p baruchstreks "' _ "$work/docker.log"
check "success: compose gets APP_DOMAIN from the same value as Caddy" has_line "$work/docker.log" "up env APP_DOMAIN=treks.example.test"
check "success: caddy block rendered with the domain" bash -c 'grep -q "^treks.example.test {" "$1" && grep -q "reverse_proxy baruchstreks-app:8080" "$1"' _ "$work/sites/baruchstreks.caddy"
check "success: no placeholder left in the block" bash -c '! grep -q "__APP_DOMAIN__" "$1"' _ "$work/sites/baruchstreks.caddy"
check "success: caddy validated and reloaded once" test "$(calls "$work/docker.log" 'caddy reload')" -eq 1
check "success: validate came before reload" bash -c '[ "$(grep -n "caddy validate" "$1" | head -1 | cut -d: -f1)" -lt "$(grep -n "caddy reload" "$1" | head -1 | cut -d: -f1)" ]' _ "$work/docker.log"
check "success: stale tag of our repo removed" has_line "$work/docker.log" "rmi $REPO:ancient"
check "success: current image kept" bash -c '! grep -q "^rmi $2$" "$1"' _ "$work/docker.log" "$NEW"
check "success: previous image kept" bash -c '! grep -q "^rmi $2$" "$1"' _ "$work/docker.log" "$OLD"
check "success: <none> entries ignored" bash -c '! grep -q "^rmi .*<none>" "$1"' _ "$work/docker.log"
check "success: no prune / system cleanup call" bash -c '! grep -Eq "prune|^system" "$1"' _ "$work/docker.log"
check "success: output does not contain secrets" bash -c '! grep -q "topsecret-conn" "$1"' _ "$work/out.txt"
check "success: backup of the block removed" test ! -e "$work/sites/baruchstreks.caddy.bak"

# 2. Unchanged block: no reload of the shared Caddy -----------------------------------------------------------------
: > "$work/docker.log"
deploy "$REPO:newsha2"
check "unchanged block: exit 0" test $? -eq 0
check "unchanged block: no caddy reload" test "$(calls "$work/docker.log" 'caddy reload')" -eq 0
check "unchanged block: no caddy validate" test "$(calls "$work/docker.log" 'caddy validate')" -eq 0
check "unchanged block: reported" grep -q "caddy block unchanged" "$work/out.txt"
check "unchanged block: previous image now the one before" test "$(cat "$work/srv/deploy/.previous-image")" = "$NEW"

# 3. Unhealthy new image: automatic rollback to the previous tag -----------------------------------------------------------
new_server
FAKE_BAD_IMAGE="$NEW" deploy "$NEW"
rc=$?
check "rollback: exit non-zero" test "$rc" -ne 0
check "rollback: APP_IMAGE restored to the previous tag" test "$(env_value APP_IMAGE)" = "$OLD"
check "rollback: compose up ran for the new image and then for the old one" bash -c '[ "$(grep -n "^up with $2$" "$1" | cut -d: -f1 | head -1)" -lt "$(grep -n "^up with $3$" "$1" | cut -d: -f1 | tail -1)" ]' _ "$work/docker.log" "$NEW" "$OLD"
check "rollback: reported" grep -q "rolling back to $OLD" "$work/out.txt"
check "rollback: caddy untouched" test ! -e "$work/sites/baruchstreks.caddy"
check "rollback: no image cleanup after a failed deploy" bash -c '! grep -q "^rmi" "$1"' _ "$work/docker.log"
check "rollback: .previous-image not written" test ! -e "$work/srv/deploy/.previous-image"

# 4. Invalid Caddy config: the previous block is restored ---------------------------------------------------------------------
new_server
printf 'previous.example.test {\n}\n' > "$work/sites/baruchstreks.caddy"
FAKE_VALIDATE_FAIL=1 deploy "$NEW"
rc=$?
check "bad caddy: exit non-zero" test "$rc" -ne 0
check "bad caddy: previous block restored" has_line "$work/sites/baruchstreks.caddy" "previous.example.test {"
check "bad caddy: backup consumed" test ! -e "$work/sites/baruchstreks.caddy.bak"
check "bad caddy: reported" grep -q "invalid Caddy configuration" "$work/out.txt"

new_server
FAKE_VALIDATE_FAIL=1 deploy "$NEW"
check "bad caddy, no previous block: new block removed again" test ! -e "$work/sites/baruchstreks.caddy"

# 5. Argument validation ----------------------------------------------------------------------------------------------------
new_server
deploy "ghcr.io/other/app:latest"
check "foreign repository rejected" test $? -eq 2
deploy "$REPO:bad tag"
check "invalid tag rejected" test $? -eq 2
deploy "$REPO:ok" "$REPO:two"
check "two image refs rejected" test $? -eq 2
deploy
check "missing image ref rejected" test $? -eq 2
deploy "$NEW" --nonsense
check "unknown option rejected" test $? -eq 2
check "nothing was executed for rejected input" test ! -s "$work/docker.log"

# 6. Preconditions ------------------------------------------------------------------------------------------------------------------
new_server
sed -i '/^APP_DOMAIN=/d' "$work/srv/deploy/.env"
deploy "$NEW"
check "no domain: exit non-zero before touching the app" test $? -ne 0
check "no domain: compose never started" bash -c '! grep -q "^compose up" "$1"' _ "$work/docker.log"
check "no domain: app image unchanged" test "$(env_value APP_IMAGE)" = "$OLD"

new_server
sed -i 's/^APP_DOMAIN=.*/APP_DOMAIN=bad domain;x/' "$work/srv/deploy/.env"
deploy "$NEW"
check "invalid domain rejected" test $? -ne 0

new_server
sed -i '/^APP_DOMAIN=/d' "$work/srv/deploy/.env"
printf 'ALLOWED_HOSTS=django.example.test,localhost\n' >> "$work/srv/deploy/.env"
deploy "$NEW"
check "Django-format .env: domain taken from ALLOWED_HOSTS" has_line "$work/sites/baruchstreks.caddy" "django.example.test {"

new_server
rm "$work/srv/deploy/.env"
deploy "$NEW"
check "missing .env: exit non-zero" test $? -ne 0

# 6b. Project pinning: legacy (Django-era) compose without `name:` and no COMPOSE_PROJECT_NAME in .env -----------------------
new_server
printf 'services:
  app:
    image: ${APP_IMAGE}
' > "$work/srv/deploy/docker-compose.yml"
sed -i '/^COMPOSE_PROJECT_NAME=/d' "$work/srv/deploy/.env"
deploy "$NEW"
check "legacy compose: deploys" test $? -eq 0
check "legacy compose: every compose call pins -p baruchstreks" bash -c '[ "$(grep -c "^compose" "$1")" -ge 2 ] && ! grep "^compose" "$1" | grep -qv "^compose -p baruchstreks "' _ "$work/docker.log"
check "legacy compose: nothing stopped/removed/downed outside compose" bash -c '! grep -Eq "^(stop|rm |kill|compose .* down)" "$1"' _ "$work/docker.log"
check "legacy compose: shared Caddy only looked up and reloaded, never recreated" bash -c '! grep -E "^compose" "$1" | grep -q "project=deploy"' _ "$work/docker.log"

new_server
sed -i 's/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=deploy/' "$work/srv/deploy/.env"
deploy "$NEW"
check "foreign COMPOSE_PROJECT_NAME: abort" test $? -ne 0
check "foreign COMPOSE_PROJECT_NAME: nothing ran" bash -c '! grep -q "^compose" "$1"' _ "$work/docker.log"
check "foreign COMPOSE_PROJECT_NAME: .env unchanged" test "$(env_value APP_IMAGE)" = "$OLD"

# 6c. Empty / absent APP_DOMAIN aborts before the running container is replaced ---------------------------------------------
new_server
sed -i 's/^APP_DOMAIN=.*/APP_DOMAIN=/' "$work/srv/deploy/.env"
deploy "$NEW"
check "empty APP_DOMAIN: abort" test $? -ne 0
check "empty APP_DOMAIN: no compose pull/up" bash -c '! grep -q "^compose" "$1"' _ "$work/docker.log"
check "empty APP_DOMAIN: app image unchanged" test "$(env_value APP_IMAGE)" = "$OLD"

# 6d. .env names a new image only after the pull succeeded ---------------------------------------------------------------------
new_server
FAKE_PULL_FAIL=1 deploy "$NEW"
check "failed compose pull: exit non-zero" test $? -ne 0
check "failed compose pull: .env still names the running image" test "$(env_value APP_IMAGE)" = "$OLD"
check "failed compose pull: compose up never ran" bash -c '! grep -q "^compose -p baruchstreks up" "$1"' _ "$work/docker.log"
new_server
deploy "$NEW"
check "pull sees the new image through the environment" has_line "$work/docker.log" "pull env APP_IMAGE=$NEW APP_DOMAIN=treks.example.test"

# 7. --selftest failure aborts before the app is touched -----------------------------------------------------------------------------
new_server
FAKE_SELFTEST_FAIL=1 deploy "$NEW" --selftest
check "selftest failure: exit non-zero" test $? -ne 0
check "selftest failure: app image unchanged" test "$(env_value APP_IMAGE)" = "$OLD"
check "selftest failure: compose never started" bash -c '! grep -q "^compose up" "$1"' _ "$work/docker.log"

if [ "$fail" -eq 0 ]; then echo "ALL PASSED"; else echo "SOME TESTS FAILED"; exit 1; fi
