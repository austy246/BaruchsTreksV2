#!/usr/bin/env bash
# Test for deploy/update-env-secrets.sh. Run: bash deploy/tests/test-update-env-secrets.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/../update-env-secrets.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail=0
ok()   { echo "ok   - $1"; }
bad()  { echo "FAIL - $1"; fail=1; }
check() { local desc="$1"; shift; if "$@"; then ok "$desc"; else bad "$desc"; fi; }
has_line() { grep -Fxq -- "$2" "$1"; }
no_prefix() { ! grep -q -- "^$2" "$1"; }
sha() { sha256sum "$1" | cut -d' ' -f1; }

env_file="$work/.env"
fresh() { printf 'COMPOSE_PROJECT_NAME=baruchstreks\nAPP_IMAGE=img\n' > "$env_file"; }
# run INPUT: feeds INPUT (printf format string) on stdin, captures stdout+stderr in $out
out="$work/out.txt"
run() { printf "$1" | ENV_FILE="$env_file" bash "$script" > "$out" 2>&1; }

# 1. GitHub secret names are translated to the ASP.NET Core keys.
fresh
run 'BARUCHSTREKS_STORAGE_CONNECTION=conn==;x\nMAPY_CZ_API_KEY=mk\nSTRAVA_CLIENT_ID=sid\nSTRAVA_CLIENT_SECRET=ssec\nADMIN_USER_1=a1\nADMIN_PASSWORD_1=p1\nADMIN_USER_2=a2\nADMIN_PASSWORD_2=p2\nAPP_DOMAIN=treks.austy.cz\n'
check "exit 0" test $? -eq 0
check "storage"        has_line "$env_file" "ConnectionStrings__Storage=conn==;x"
check "mapy"           has_line "$env_file" "Mapy__ApiKey=mk"
check "strava id"      has_line "$env_file" "Strava__ClientId=sid"
check "strava secret"  has_line "$env_file" "Strava__ClientSecret=ssec"
check "admin 1 user"   has_line "$env_file" "Admin__Users__0__UserName=a1"
check "admin 1 pass"   has_line "$env_file" "Admin__Users__0__Password=p1"
check "admin 2 user"   has_line "$env_file" "Admin__Users__1__UserName=a2"
check "admin 2 pass"   has_line "$env_file" "Admin__Users__1__Password=p2"
check "APP_DOMAIN"     has_line "$env_file" "APP_DOMAIN=treks.austy.cz"
check "other lines untouched" has_line "$env_file" "APP_IMAGE=img"
check "output has no values" bash -c '! grep -Eq "conn==|mk|sid|ssec|p1|p2|a1|a2" "$1"' _ "$out"
check "output names keys" grep -q "^set Admin__Users__0__Password$" "$out"

# 2. Legacy DJANGO_SUPERUSER_* secrets map to index 0 when ADMIN_USER_1 is empty or absent.
fresh
run 'DJANGO_SUPERUSER_USERNAME=legacyu\nDJANGO_SUPERUSER_PASSWORD=legacyp\nADMIN_USER_1=\nADMIN_PASSWORD_1=\n'
check "legacy user -> index 0" has_line "$env_file" "Admin__Users__0__UserName=legacyu"
check "legacy password -> index 0" has_line "$env_file" "Admin__Users__0__Password=legacyp"
check "legacy output has no values" bash -c '! grep -Eq "legacyu|legacyp" "$1"' _ "$out"

# 3. ADMIN_USER_1 set: legacy values are ignored.
fresh
run 'ADMIN_USER_1=new1\nADMIN_PASSWORD_1=newp\nDJANGO_SUPERUSER_USERNAME=legacyu\nDJANGO_SUPERUSER_PASSWORD=legacyp\n'
check "ADMIN_USER_1 wins" has_line "$env_file" "Admin__Users__0__UserName=new1"
check "ADMIN_PASSWORD_1 wins" has_line "$env_file" "Admin__Users__0__Password=newp"
check "legacy not written" bash -c '! grep -q "legacy" "$1"' _ "$env_file"

# 4. Empty values never overwrite what is on the server; non-empty ones replace (single line, no duplicates).
printf 'COMPOSE_PROJECT_NAME=baruchstreks\nConnectionStrings__Storage=keepme\nMapy__ApiKey=old\n' > "$env_file"
run 'BARUCHSTREKS_STORAGE_CONNECTION=\nMAPY_CZ_API_KEY=newkey\nSTRAVA_CLIENT_ID=\n'
check "empty secret keeps server value" has_line "$env_file" "ConnectionStrings__Storage=keepme"
check "non-empty secret replaces"       has_line "$env_file" "Mapy__ApiKey=newkey"
check "no duplicate Mapy__ApiKey"       test "$(grep -c '^Mapy__ApiKey=' "$env_file")" = 1
check "empty strava creates nothing"    no_prefix "$env_file" "Strava__"

# 5. Not allow-listed keys are rejected (name reported, value never), file unchanged by them.
fresh
before="$(sha "$env_file")"
run 'APP_IMAGE=evil-image\nCOMPOSE_PROJECT_NAME=other\nSOME_TOKEN=topsecretvalue\nnot-a-pair\n'
check "exit 0 when everything is skipped" test $? -eq 0
check "file unchanged" test "$before" = "$(sha "$env_file")"
check "rejected key name reported" grep -q "skipping key not allowed: SOME_TOKEN" "$out"
check "rejected value not echoed" bash -c '! grep -q "topsecretvalue" "$1"' _ "$out"
check "malformed line reported without content" grep -q "skipping malformed line" "$out"
check "malformed line content not echoed" bash -c '! grep -q "not-a-pair" "$1"' _ "$out"

# 6. Direct final names are accepted and win over an alias; CRLF input and a missing final newline are handled.
fresh
printf 'ADMIN_USER_1=viaalias\r\nAdmin__Users__0__UserName=direct\r\nApp_x=1\r\nMAPY_CZ_API_KEY=lastline' | ENV_FILE="$env_file" bash "$script" > "$out" 2>&1
check "direct name wins over alias" has_line "$env_file" "Admin__Users__0__UserName=direct"
check "last line without newline is read" has_line "$env_file" "Mapy__ApiKey=lastline"
check "CRLF stripped" bash -c '! grep -q $'"'"'\r'"'"' "$1"' _ "$env_file"

# 7. File without a trailing newline: the new key lands on its own line.
printf 'APP_IMAGE=img' > "$env_file"
run 'MAPY_CZ_API_KEY=k\n'
check "old last line intact" has_line "$env_file" "APP_IMAGE=img"
check "new key on its own line" has_line "$env_file" "Mapy__ApiKey=k"

# 8. A missing env file is an error (never creates one).
rm -f "$env_file"
printf 'MAPY_CZ_API_KEY=k\n' | ENV_FILE="$env_file" bash "$script" > "$out" 2>&1
check "missing env file -> non-zero exit" test $? -ne 0
check "env file not created" test ! -e "$env_file"

# 9. No secret ever appears in argv of the script itself (it only reads stdin).
check "script never reads positional arguments" bash -c '! grep -Eq "[$][1@*]" "$1"' _ "$script"

if [ "$fail" -eq 0 ]; then echo "ALL PASSED"; else echo "SOME TESTS FAILED"; exit 1; fi
