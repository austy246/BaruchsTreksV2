#!/usr/bin/env bash
# Merges KEY=VALUE lines read from STDIN into deploy/.env. Secrets arrive on stdin only (never in argv) and
# only key NAMES are ever printed. Only allow-listed keys are accepted; an empty value never overwrites what is
# already in the file (a GitHub secret that does not exist is an empty string and must not blank a working value).
# Other lines in the file are left untouched.
#
# Accepted input names (GitHub secret names) and the ASP.NET Core key they are written to:
#   BARUCHSTREKS_STORAGE_CONNECTION -> ConnectionStrings__Storage      MAPY_CZ_API_KEY -> Mapy__ApiKey
#   STRAVA_CLIENT_ID / _SECRET      -> Strava__ClientId / Strava__ClientSecret
#   ADMIN_USER_1 / ADMIN_PASSWORD_1 -> Admin__Users__0__UserName / Admin__Users__0__Password
#   ADMIN_USER_2 / ADMIN_PASSWORD_2 -> Admin__Users__1__UserName / Admin__Users__1__Password
#   DJANGO_SUPERUSER_USERNAME / _PASSWORD -> index 0, but only while ADMIN_USER_1 is empty (legacy secrets)
#   APP_DOMAIN                      -> APP_DOMAIN
# The final ASP.NET Core names (and APP_DOMAIN) are accepted as they are too and win over an alias.
set -euo pipefail
umask 077

ENV_FILE="${ENV_FILE:-$(dirname "$0")/.env}"

DIRECT_KEYS="ConnectionStrings__Storage Mapy__ApiKey Strava__ClientId Strava__ClientSecret Admin__Users__0__UserName Admin__Users__0__Password Admin__Users__1__UserName Admin__Users__1__Password APP_DOMAIN"
ALIASES="BARUCHSTREKS_STORAGE_CONNECTION:ConnectionStrings__Storage MAPY_CZ_API_KEY:Mapy__ApiKey STRAVA_CLIENT_ID:Strava__ClientId STRAVA_CLIENT_SECRET:Strava__ClientSecret ADMIN_USER_1:Admin__Users__0__UserName ADMIN_PASSWORD_1:Admin__Users__0__Password ADMIN_USER_2:Admin__Users__1__UserName ADMIN_PASSWORD_2:Admin__Users__1__Password"
LEGACY_ADMIN="DJANGO_SUPERUSER_USERNAME:Admin__Users__0__UserName DJANGO_SUPERUSER_PASSWORD:Admin__Users__0__Password"

[ -f "$ENV_FILE" ] || { echo "missing $ENV_FILE" >&2; exit 1; }

allowed=" $DIRECT_KEYS "
for pair in $ALIASES $LEGACY_ADMIN; do allowed+="${pair%%:*} "; done

declare -A got=() out=()
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%$'\r'}"   # tolerate CRLF in a secrets.env written on Windows
  [ -z "$line" ] && continue
  case "$line" in *=*) ;; *) echo "skipping malformed line" >&2; continue ;; esac   # never echo a line without a key
  key="${line%%=*}"
  case "$allowed" in
    *" $key "*) got["$key"]="${line#*=}" ;;
    *) echo "skipping key not allowed: $key" >&2 ;;
  esac
done

for pair in $ALIASES; do
  value="${got[${pair%%:*}]:-}"
  [ -n "$value" ] && out["${pair#*:}"]="$value"
done
if [ -z "${got[ADMIN_USER_1]:-}" ]; then
  for pair in $LEGACY_ADMIN; do
    value="${got[${pair%%:*}]:-}"
    [ -n "$value" ] && [ -z "${out[${pair#*:}]:-}" ] && out["${pair#*:}"]="$value"
  done
fi
for key in $DIRECT_KEYS; do
  value="${got[$key]:-}"
  [ -n "$value" ] && out["$key"]="$value"
done

tmp="$(mktemp "${ENV_FILE}.XXXXXX")"
trap 'rm -f "$tmp" "$tmp.new"' EXIT
cp "$ENV_FILE" "$tmp"

for key in $DIRECT_KEYS; do
  [ -n "${out[$key]:-}" ] || continue
  { grep -v "^${key}=" "$tmp" || true; } > "$tmp.new"
  if [ -s "$tmp.new" ] && [ -n "$(tail -c1 "$tmp.new")" ]; then printf '\n' >> "$tmp.new"; fi
  printf '%s=%s\n' "$key" "${out[$key]}" >> "$tmp.new"
  mv "$tmp.new" "$tmp"
  echo "set $key"
done

mv "$tmp" "$ENV_FILE"
