#!/usr/bin/env bash
# Test for deploy/migrate-env.sh. Run: bash deploy/tests/test-migrate-env.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/../migrate-env.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail=0
ok()   { echo "ok   - $1"; }
bad()  { echo "FAIL - $1"; fail=1; }
check() { # description, command...
  local desc="$1"; shift
  if "$@"; then ok "$desc"; else bad "$desc"; fi
}
has_line()  { grep -Fxq -- "$2" "$1"; }
no_prefix() { ! grep -q -- "^$2" "$1"; }
count_baks() { ls "$1"/.env.bak-* 2>/dev/null | wc -l; }
legacy_keys() { grep -E '^(SECRET_KEY|DEBUG|ALLOWED_HOSTS|CSRF_TRUSTED_ORIGINS|BARUCHSTREKS_STORAGE_CONNECTION|MAPY_CZ_API_KEY|STRAVA_[A-Z_]*|DJANGO_[A-Z_]*|APP_IMAGE|COMPOSE_PROJECT_NAME)=' "$1"; }
sha() { sha256sum "$1" | cut -d' ' -f1; }

CONN='DefaultEndpointsProtocol=https;AccountName=baruchstreks;AccountKey=abc==;EndpointSuffix=core.windows.net'
SECRETS_RE='abc==|mapykey123|stravaid77|stravasecret88|pw-secret-1'
env_file="$work/.env"
write_env() {
  cat > "$env_file" <<EOF2
COMPOSE_PROJECT_NAME=baruchstreks
SECRET_KEY=s3cretkey
DEBUG=False
ALLOWED_HOSTS=treks.austy.cz,localhost,127.0.0.1
CSRF_TRUSTED_ORIGINS=https://treks.austy.cz
BARUCHSTREKS_STORAGE_CONNECTION=$CONN
MAPY_CZ_API_KEY=mapykey123
STRAVA_CLIENT_ID=stravaid77
STRAVA_CLIENT_SECRET=stravasecret88
DJANGO_SUPERUSER_USERNAME=baruch
DJANGO_SUPERUSER_PASSWORD=pw-secret-1
APP_IMAGE=x
EOF2
}

write_env
legacy_before="$work/legacy-before.txt"
legacy_keys "$env_file" > "$legacy_before"

out1="$work/out1.txt"
bash "$script" "$env_file" > "$out1" 2>&1
check "first run exits 0" test $? -eq 0

check "ConnectionStrings__Storage copied intact (incl. ==)" has_line "$env_file" "ConnectionStrings__Storage=$CONN"
check "Mapy__ApiKey"                has_line "$env_file" "Mapy__ApiKey=mapykey123"
check "Admin__Users__0__UserName"   has_line "$env_file" "Admin__Users__0__UserName=baruch"
check "Admin__Users__0__Password"   has_line "$env_file" "Admin__Users__0__Password=pw-secret-1"
check "Strava__ClientId"            has_line "$env_file" "Strava__ClientId=stravaid77"
check "Strava__ClientSecret"        has_line "$env_file" "Strava__ClientSecret=stravasecret88"
check "APP_DOMAIN = first ALLOWED_HOSTS entry" has_line "$env_file" "APP_DOMAIN=treks.austy.cz"
check "Storage__PublicBlobBaseUrl"  has_line "$env_file" "Storage__PublicBlobBaseUrl=https://baruchstreks.blob.core.windows.net/photos/"
check "ASPNETCORE_ENVIRONMENT"      has_line "$env_file" "ASPNETCORE_ENVIRONMENT=Production"
check "no AllowedHosts in .env (compose derives it from APP_DOMAIN)" no_prefix "$env_file" "AllowedHosts="
check "no second admin created from nothing" no_prefix "$env_file" "Admin__Users__1__"

# Old variables are all still there, unchanged.
legacy_after="$work/legacy-after.txt"
legacy_keys "$env_file" > "$legacy_after"
check "old variables left in place, unchanged" diff -q "$legacy_before" "$legacy_after"

# File modes are not meaningful on Windows filesystems (Git Bash), so only check them elsewhere.
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) echo "skip - file modes (Windows filesystem)" ;;
  *) check "env file mode is 600" bash -c '[ "$(stat -c %a "$1")" = 600 ]' _ "$env_file" ;;
esac
check "backup created" test "$(count_baks "$work")" -eq 1

# Nothing sensitive reaches stdout/stderr: only key names and status lines.
check "output hides every secret value" bash -c '! grep -Eq "$2" "$1"' _ "$out1" "$SECRETS_RE"
check "output names the keys it set"   grep -q "^set ConnectionStrings__Storage$" "$out1"

# stdout alone (stderr dropped) must not contain values either, on a fresh env.
write_env
stdout_only="$work/stdout-only.txt"
bash "$script" "$env_file" 2>/dev/null > "$stdout_only"
check "stdout alone has no secret values or domain" bash -c '! grep -Eq "$2|treks.austy.cz" "$1"' _ "$stdout_only" "$SECRETS_RE"
rm -f "$work"/.env.bak-*

# Idempotence: after a migration a second run changes nothing and creates no new backup.
write_env
bash "$script" "$env_file" > /dev/null 2>&1
baks="$(count_baks "$work")"
sum1="$(sha "$env_file")"
out2="$work/out2.txt"
bash "$script" "$env_file" > "$out2" 2>&1
check "second run exits 0" test $? -eq 0
check "second run leaves the file unchanged" test "$sum1" = "$(sha "$env_file")"
check "second run creates no new backup" test "$(count_baks "$work")" -eq "$baks"
check "second run says nothing to change" grep -q "^nothing to change$" "$out2"
check "second run output hides secrets" bash -c '! grep -Eq "$2" "$1"' _ "$out2" "$SECRETS_RE"

# Existing new keys are never overwritten (even when the old key has another value).
sed -i 's/^Mapy__ApiKey=.*/Mapy__ApiKey=already-new/' "$env_file"
bash "$script" "$env_file" > /dev/null 2>&1
check "existing Mapy__ApiKey kept" has_line "$env_file" "Mapy__ApiKey=already-new"
check "no second Mapy__ApiKey"     test "$(grep -c '^Mapy__ApiKey=' "$env_file")" = 1

# An empty new key is filled from the old one.
sed -i 's/^Mapy__ApiKey=.*/Mapy__ApiKey=/' "$env_file"
bash "$script" "$env_file" > /dev/null 2>&1
check "empty Mapy__ApiKey filled from the old key" has_line "$env_file" "Mapy__ApiKey=mapykey123"

# Absent source values create nothing: no Strava, no Mapy, no admin; the required keys are still migrated.
cat > "$env_file" <<EOF2
ALLOWED_HOSTS=other.example.org
BARUCHSTREKS_STORAGE_CONNECTION=$CONN
EOF2
out4="$work/out4.txt"
bash "$script" "$env_file" > "$out4" 2>&1
check "minimal env migrates (exit 0)" test $? -eq 0
check "APP_DOMAIN from ALLOWED_HOSTS" has_line "$env_file" "APP_DOMAIN=other.example.org"
check "no Strava__ClientId created"   no_prefix "$env_file" "Strava__ClientId"
check "no Strava__ClientSecret created" no_prefix "$env_file" "Strava__ClientSecret"
check "no Mapy__ApiKey created"       no_prefix "$env_file" "Mapy__ApiKey"
check "no admin keys created"         no_prefix "$env_file" "Admin__Users__"
check "absent optional values only warn" grep -q "warning: Mapy__ApiKey is empty" "$out4"

# A file without a trailing newline does not glue the new key onto the last line.
printf 'ALLOWED_HOSTS=a.example\nBARUCHSTREKS_STORAGE_CONNECTION=c1' > "$env_file"
bash "$script" "$env_file" > /dev/null 2>&1
check "no trailing newline: last old key intact" has_line "$env_file" "BARUCHSTREKS_STORAGE_CONNECTION=c1"
check "no trailing newline: new key on its own line" has_line "$env_file" "ConnectionStrings__Storage=c1"

# CRLF input (edited on Windows) is normalised.
printf 'ALLOWED_HOSTS=a.example\r\nBARUCHSTREKS_STORAGE_CONNECTION=c2\r\n' > "$env_file"
bash "$script" "$env_file" > /dev/null 2>&1
check "CRLF input: value has no carriage return" has_line "$env_file" "ConnectionStrings__Storage=c2"

# --drop-legacy removes the Django keys but keeps the rest, and only on top of a complete migration.
write_env
bash "$script" "$env_file" > /dev/null 2>&1
out3="$work/out3.txt"
bash "$script" --drop-legacy "$env_file" > "$out3" 2>&1
check "--drop-legacy exits 0" test $? -eq 0
for k in SECRET_KEY DEBUG ALLOWED_HOSTS CSRF_TRUSTED_ORIGINS BARUCHSTREKS_STORAGE_CONNECTION MAPY_CZ_API_KEY STRAVA_CLIENT_ID STRAVA_CLIENT_SECRET DJANGO_; do
  check "$k removed" no_prefix "$env_file" "$k"
done
check "APP_IMAGE=x kept" has_line "$env_file" "APP_IMAGE=x"
check "COMPOSE_PROJECT_NAME kept" has_line "$env_file" "COMPOSE_PROJECT_NAME=baruchstreks"
check "new keys survive --drop-legacy" has_line "$env_file" "ConnectionStrings__Storage=$CONN"
check "Admin password survives --drop-legacy" has_line "$env_file" "Admin__Users__0__Password=pw-secret-1"
check "APP_DOMAIN survives --drop-legacy" has_line "$env_file" "APP_DOMAIN=treks.austy.cz"
check "--drop-legacy output hides secrets" bash -c '! grep -Eq "$2" "$1"' _ "$out3" "$SECRETS_RE"

# Without a usable source for the connection string the script fails and changes nothing.
printf 'ALLOWED_HOSTS=a.example,b\nMAPY_CZ_API_KEY=k\n' > "$env_file"
before="$(sha "$env_file")"
bash "$script" --drop-legacy "$env_file" > /dev/null 2>&1
check "missing connection string -> non-zero exit" test $? -ne 0
check "file unchanged on failure" test "$before" = "$(sha "$env_file")"
check "nothing dropped on failure" has_line "$env_file" "MAPY_CZ_API_KEY=k"

if [ "$fail" -eq 0 ]; then echo "ALL PASSED"; else echo "SOME TESTS FAILED"; exit 1; fi
