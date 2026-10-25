#!/usr/bin/env bash
# Migrates deploy/.env from the Django app's keys to the ASP.NET Core ones. Idempotent: it only fills new keys that
# are missing or empty, never overwrites a value, never creates a key whose source value is absent, and leaves the
# file (and the backups) alone when nothing changes. The OLD keys stay in place (a rollback to the Django image
# still works) until --drop-legacy is run after the owner has verified the new version. Prints key names only,
# never values.
#
#   BARUCHSTREKS_STORAGE_CONNECTION -> ConnectionStrings__Storage     MAPY_CZ_API_KEY -> Mapy__ApiKey
#   DJANGO_SUPERUSER_USERNAME/_PASSWORD -> Admin__Users__0__UserName/_Password
#   STRAVA_CLIENT_ID/_SECRET -> Strava__ClientId/_ClientSecret         first entry of ALLOWED_HOSTS -> APP_DOMAIN
#   plus Storage__PublicBlobBaseUrl and ASPNETCORE_ENVIRONMENT=Production.
#
# Usage: migrate-env.sh [--drop-legacy] [ENV_FILE]      (ENV_FILE defaults to .env next to this script)
#   --drop-legacy  after a successful migration, remove the old keys (BARUCHSTREKS_STORAGE_CONNECTION,
#                  MAPY_CZ_API_KEY, STRAVA_*, DJANGO_*, ALLOWED_HOSTS, CSRF_TRUSTED_ORIGINS, SECRET_KEY, DEBUG).
#                  A rollback to the Django image then needs the backup. Only after the owner's approval.
set -euo pipefail
umask 077

# ACCOUNT base URL (the app appends "photos/{trip}/{name}"); NOT the container URL.
PUBLIC_BLOB_BASE_URL="https://baruchstreks.blob.core.windows.net/"

drop_legacy=0
ENV_FILE=""
for arg in "$@"; do
  case "$arg" in
    --drop-legacy) drop_legacy=1 ;;
    -*) echo "unknown option: $arg" >&2; exit 2 ;;
    *) ENV_FILE="$arg" ;;
  esac
done
ENV_FILE="${ENV_FILE:-$(dirname "$0")/.env}"
[ -f "$ENV_FILE" ] || { echo "missing $ENV_FILE" >&2; exit 1; }

tmp="$(mktemp "${ENV_FILE}.XXXXXX")"
trap 'rm -f "$tmp" "$tmp.new"' EXIT
tr -d '\r' < "$ENV_FILE" > "$tmp"

# Value of KEY in the working copy (empty when absent). Splits on the first '=' only.
get() { local line; line="$(grep -m1 "^$1=" "$tmp" || true)"; printf '%s' "${line#*=}"; }
# Sets KEY to VALUE (replacing any existing line). Logs only the key name via the caller.
changed=0
put() {
  local key="$1" value="$2"
  { grep -v "^${key}=" "$tmp" || true; } > "$tmp.new"
  if [ -s "$tmp.new" ] && [ -n "$(tail -c1 "$tmp.new")" ]; then printf '
' >> "$tmp.new"; fi
  printf '%s=%s
' "$key" "$value" >> "$tmp.new"
  mv "$tmp.new" "$tmp"
  changed=1
}
# Sets KEY unless it already has a non-empty value; $2 is the value (an empty one creates nothing). Logs only the key name.
fill() {
  local key="$1" value="$2"
  [ -n "$(get "$key")" ] && return 0
  [ -n "$value" ] || return 0
  put "$key" "$value"
  echo "set $key"
}

first_host="$(get ALLOWED_HOSTS | cut -d, -f1 | tr -d '[:space:]')"
fill ConnectionStrings__Storage "$(get BARUCHSTREKS_STORAGE_CONNECTION)"
fill Mapy__ApiKey "$(get MAPY_CZ_API_KEY)"
fill Admin__Users__0__UserName "$(get DJANGO_SUPERUSER_USERNAME)"
fill Admin__Users__0__Password "$(get DJANGO_SUPERUSER_PASSWORD)"
fill Strava__ClientId "$(get STRAVA_CLIENT_ID)"
fill Strava__ClientSecret "$(get STRAVA_CLIENT_SECRET)"
fill APP_DOMAIN "$first_host"
fill Storage__PublicBlobBaseUrl "$PUBLIC_BLOB_BASE_URL"
fill ASPNETCORE_ENVIRONMENT Production

# An earlier version wrote the CONTAINER url (.../photos/); the app needs the account base, otherwise photo
# urls become .../photos/photos/... (404). Only that exact container-style shape is rewritten; anything else is the owner's.
current_blob_base="$(get Storage__PublicBlobBaseUrl)"
case "$current_blob_base" in
  */photos/|*/photos)
    fixed_blob_base="${current_blob_base%/}"
    fixed_blob_base="${fixed_blob_base%/photos}/"
    put Storage__PublicBlobBaseUrl "$fixed_blob_base"
    echo "fixed Storage__PublicBlobBaseUrl (account base, not container)"
    ;;
esac

# Keys the app cannot run (or be routed) without. The others are optional and only warn.
missing=0
for key in ConnectionStrings__Storage APP_DOMAIN; do
  if [ -z "$(get "$key")" ]; then echo "MISSING $key (no source value found)" >&2; missing=1; fi
done
for key in Admin__Users__0__UserName Admin__Users__0__Password Mapy__ApiKey; do
  [ -n "$(get "$key")" ] || echo "warning: $key is empty" >&2
done
if [ "$missing" -ne 0 ]; then
  echo "migration incomplete, $ENV_FILE left unchanged" >&2
  exit 1
fi

if [ "$drop_legacy" -eq 1 ]; then
  before="$(wc -l < "$tmp")"
  { grep -Ev '^(BARUCHSTREKS_STORAGE_CONNECTION|MAPY_CZ_API_KEY|STRAVA_CLIENT_ID|STRAVA_CLIENT_SECRET|DJANGO_[A-Za-z0-9_]*|ALLOWED_HOSTS|CSRF_TRUSTED_ORIGINS|SECRET_KEY|DEBUG)=' "$tmp" || true; } > "$tmp.new"
  mv "$tmp.new" "$tmp"
  if [ "$before" -ne "$(wc -l < "$tmp")" ]; then changed=1; echo "dropped legacy keys"; fi
fi

# Also rewritten (and backed up) when only CRLF line endings were normalised.
if [ "$changed" -eq 0 ] && cmp -s "$tmp" "$ENV_FILE"; then
  echo "nothing to change"
  exit 0
fi

backup="${ENV_FILE%/*}"
[ "$backup" = "$ENV_FILE" ] && backup="."
backup="$backup/$(basename "$ENV_FILE").bak-$(date +%Y%m%d%H%M%S)"
cp -p "$ENV_FILE" "$backup"
chmod 600 "$backup" "$tmp"
mv "$tmp" "$ENV_FILE"
echo "updated $ENV_FILE (backup: $(basename "$backup"))"
