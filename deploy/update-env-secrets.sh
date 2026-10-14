#!/usr/bin/env bash
# Merges KEY=VALUE lines read from STDIN into deploy/.env. Secrets arrive on stdin only (never in
# argv). Only allow-listed keys are written; empty values are skipped, so a missing GitHub secret
# never blanks a value that works. Other lines in the file are left untouched.
set -euo pipefail
umask 077

ENV_FILE="${ENV_FILE:-$(dirname "$0")/.env}"
ALLOWED_KEYS="${ALLOWED_KEYS:-BARUCHSTREKS_STORAGE_CONNECTION MAPY_CZ_API_KEY STRAVA_CLIENT_ID STRAVA_CLIENT_SECRET DJANGO_SUPERUSER_USERNAME DJANGO_SUPERUSER_PASSWORD}"

[ -f "$ENV_FILE" ] || { echo "missing $ENV_FILE" >&2; exit 1; }

tmp="$(mktemp "${ENV_FILE}.XXXXXX")"
trap 'rm -f "$tmp" "$tmp.new"' EXIT
cp "$ENV_FILE" "$tmp"

while IFS= read -r line || [ -n "$line" ]; do
  [ -z "$line" ] && continue
  key="${line%%=*}"
  value="${line#*=}"
  case " $ALLOWED_KEYS " in
    *" $key "*) ;;
    *) echo "skipping key not allowed: $key" >&2; continue ;;
  esac
  [ -z "$value" ] && continue
  grep -v "^${key}=" "$tmp" > "$tmp.new" || true
  printf '%s=%s\n' "$key" "$value" >> "$tmp.new"
  mv "$tmp.new" "$tmp"
done

mv "$tmp" "$ENV_FILE"
