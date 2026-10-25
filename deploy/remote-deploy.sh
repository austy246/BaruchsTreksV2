#!/usr/bin/env bash
# Runs ON THE VPS, fed over SSH from the local machine by deploy/deploy.ps1:
#   ssh ... deploy@host bash -s -- IMAGE_REF [--selftest] [--prepare-only] < remote-deploy.sh
#
#   IMAGE_REF        ghcr.io/austy246/baruchstreks-app:<tag>
#   --selftest       first run `IMAGE_REF --selftest` (native dependencies of the image); failure aborts
#   --prepare-only   do not deploy: migrate deploy/.env (migrate-env.sh) and run the read-only
#                    `IMAGE_REF --check-data` against production storage, then stop
#
# The VPS is shared with other projects: this script only ever touches baruchstreks. It never does a
# host-wide image/volume cleanup; it removes only old tags of ghcr.io/austy246/baruchstreks-app
# (explicit `docker rmi` of our own repository). deploy/tests/test-deploy-files.sh enforces this.
# Secrets are never printed and never passed in argv (the env file is only handed to docker via --env-file).
#
# The whole body is a function so bash has parsed it completely before running anything (the script
# arrives on stdin, and nothing below may read stdin: docker commands get </dev/null).
set -euo pipefail

# Rewrites APP_IMAGE in .env (cwd) without sed, so the value can never be read as a pattern.
set_app_image() {
  local tmp
  tmp="$(mktemp .env.XXXXXX)"
  { grep -v '^APP_IMAGE=' .env || true; } > "$tmp"
  printf 'APP_IMAGE=%s\n' "$1" >> "$tmp"
  mv "$tmp" .env
}

main() {
  local APP_DIR="${APP_DIR:-/srv/baruchstreks}"
  # Pinned on EVERY compose call: a restored Django-era compose has no `name:`, and without a project name compose
  # would fall back to the directory name "deploy" = the project of the shared Caddy (--remove-orphans would
  # then remove it).
  local PROJECT="baruchstreks"
  local IMAGE_REPO="ghcr.io/austy246/baruchstreks-app"
  local SITES="${SITES:-/srv/infra/sites}"

  local image_ref="" selftest=0 prepare_only=0 arg
  for arg in "$@"; do
    case "$arg" in
      --selftest) selftest=1 ;;
      --prepare-only) prepare_only=1 ;;
      -*) echo "ERROR: unknown option $arg" >&2; exit 2 ;;
      *) [ -z "$image_ref" ] || { echo "ERROR: more than one IMAGE_REF" >&2; exit 2; }; image_ref="$arg" ;;
    esac
  done
  [ -n "$image_ref" ] || { echo "usage: remote-deploy.sh IMAGE_REF [--selftest] [--prepare-only]" >&2; exit 2; }
  # The ref ends up in .env and in docker commands, so accept nothing but our own repository and a plain tag.
  case "$image_ref" in
    "$IMAGE_REPO":*) ;;
    *) echo "ERROR: IMAGE_REF must be $IMAGE_REPO:<tag>" >&2; exit 2 ;;
  esac
  [[ "${image_ref#"$IMAGE_REPO":}" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]] \
    || { echo "ERROR: invalid image tag" >&2; exit 2; }

  # 1. .env must exist (it holds the secrets and COMPOSE_PROJECT_NAME=baruchstreks; the existing server has it).
  cd "$APP_DIR/deploy"
  [ -f .env ] || {
    echo "ERROR: $APP_DIR/deploy/.env is missing. Create it with COMPOSE_PROJECT_NAME=baruchstreks and APP_DOMAIN=<domain>," >&2
    echo "       then fill the secrets (deploy.ps1 -UpdateSecrets or the deploy workflow) before deploying." >&2
    exit 1
  }

  # 2. Anonymous pull (the GHCR package is public).
  echo "==> docker pull $image_ref"
  docker pull "$image_ref" </dev/null

  # 3. Native dependencies of the image (Magick.NET, HEIC, WebP, ICU); needs neither storage nor network.
  if [ "$selftest" -eq 1 ]; then
    echo "==> selftest"
    docker run --rm --memory 512m --cpus 1 "$image_ref" --selftest </dev/null || { echo "ERROR: selftest failed" >&2; exit 1; }
  fi

  # 4. Prepare only: convert .env and prove the new image can read every production entity (read-only).
  if [ "$prepare_only" -eq 1 ]; then
    echo "==> migrate-env"
    bash ./migrate-env.sh
    echo "==> check-data (read-only)"
    docker run --rm --memory 512m --cpus 1 --env-file .env "$image_ref" --check-data </dev/null
    echo "prepare-only finished, nothing was deployed"
    return 0
  fi

  # 4a. Never run compose under another project: an explicit COMPOSE_PROJECT_NAME that differs is a misconfiguration.
  local env_project
  env_project="$(grep -m1 '^COMPOSE_PROJECT_NAME=' .env | cut -d= -f2- || true)"
  if [ -n "$env_project" ] && [ "$env_project" != "$PROJECT" ]; then
    echo "ERROR: COMPOSE_PROJECT_NAME in .env is not $PROJECT" >&2
    exit 1
  fi

  # 4b. The domain feeds AllowedHosts (compose) and the Caddy block, so check it before touching the running app.
  # APP_DOMAIN is the source; ALLOWED_HOSTS (first entry) is the fallback for a .env that is still in the Django
  # format, which is what a rollback to a Django image needs.
  local domain
  domain="$(grep -m1 '^APP_DOMAIN=' .env | cut -d= -f2- || true)"
  [ -n "$domain" ] || domain="$(grep -m1 '^ALLOWED_HOSTS=' .env | cut -d= -f2- | cut -d, -f1 || true)"
  [[ "$domain" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] \
    || { echo "ERROR: APP_DOMAIN (or ALLOWED_HOSTS) in .env is missing or invalid" >&2; exit 1; }
  # The same value feeds compose (AllowedHosts) and the Caddy block, so they can never disagree.
  export APP_DOMAIN="$domain"

  # 5. Deploy. APP_IMAGE is rewritten without sed so the value can never be interpreted as a pattern.
  # If the new container does not become healthy, the image that ran before is started again.
  local current_ref previous
  current_ref="$(grep -m1 '^APP_IMAGE=' .env | cut -d= -f2- || true)"
  previous="$(cat .previous-image 2>/dev/null || true)"
  [ -n "$current_ref" ] && [ "$current_ref" != "$image_ref" ] && previous="$current_ref"
  umask 077

  echo "==> docker compose up"
  # .env is changed only after the pull succeeded, so it never names an image that did not arrive.
  APP_IMAGE="$image_ref" docker compose -p "$PROJECT" pull app </dev/null
  set_app_image "$image_ref"
  if ! docker compose -p "$PROJECT" up -d --wait --wait-timeout 180 --remove-orphans </dev/null; then
    echo "ERROR: the app did not become healthy." >&2
    docker compose -p "$PROJECT" ps </dev/null >&2 || true
    if [ -n "$current_ref" ] && [ "$current_ref" != "$image_ref" ]; then
      echo "==> rolling back to $current_ref" >&2
      set_app_image "$current_ref"
      docker compose -p "$PROJECT" up -d --wait --wait-timeout 180 --remove-orphans </dev/null \
        || echo "ERROR: rollback did not become healthy either" >&2
    fi
    exit 1
  fi
  # Remember the last good image only once the new one is healthy (quick manual rollback target).
  if [ -n "$previous" ] && [ "$previous" != "$image_ref" ]; then printf '%s\n' "$previous" > .previous-image; fi

  # 6. Shared Caddy (the one of manarozeniny imports $SITES/*.caddy): drop our block in, validate the whole
  # config, reload only if valid. An invalid block is rolled back, so it can never break the other
  # projects' next reload. The block is rendered from the committed baruchstreks.caddy with the domain from 4b.
  echo "==> caddy"
  [ -d "$SITES" ] || { echo "ERROR: missing $SITES" >&2; exit 1; }
  local caddy
  caddy="$(docker ps -q --filter label=com.docker.compose.project=deploy --filter label=com.docker.compose.service=caddy | head -1)"
  [ -n "$caddy" ] || { echo "ERROR: Caddy container (project deploy) not found" >&2; exit 1; }
  local rendered
  rendered="$(mktemp)"
  sed "s#__APP_DOMAIN__#${domain}#g" "$APP_DIR/deploy/baruchstreks.caddy" > "$rendered"
  if [ -f "$SITES/baruchstreks.caddy" ] && cmp -s "$rendered" "$SITES/baruchstreks.caddy"; then
    # Unchanged block: do not reload the Caddy shared by the other projects on every deploy.
    rm -f "$rendered"
    echo "caddy block unchanged, no reload"
  else
    if [ -f "$SITES/baruchstreks.caddy" ]; then cp -p "$SITES/baruchstreks.caddy" "$SITES/baruchstreks.caddy.bak"; fi
    cat "$rendered" > "$SITES/baruchstreks.caddy"
    rm -f "$rendered"
    chmod 644 "$SITES/baruchstreks.caddy"
    # The validator output can contain the other projects' shared Caddy config, so it never reaches stdout/stderr
    # (the CI log): it goes to a 600 file in the deploy dir, deleted again after a successful reload.
    local caddy_log="$APP_DIR/deploy/caddy-validate.log"
    ( umask 077; : > "$caddy_log" )
    chmod 600 "$caddy_log"
    if docker exec "$caddy" caddy validate --config /etc/caddy/Caddyfile >"$caddy_log" 2>&1 </dev/null        && docker exec "$caddy" caddy reload --config /etc/caddy/Caddyfile >>"$caddy_log" 2>&1 </dev/null; then
      rm -f "$SITES/baruchstreks.caddy.bak" "$caddy_log"
    else
      echo "ERROR: invalid Caddy configuration, restoring the previous file (validator output saved on the server in $caddy_log)" >&2
      if [ -f "$SITES/baruchstreks.caddy.bak" ]; then
        mv "$SITES/baruchstreks.caddy.bak" "$SITES/baruchstreks.caddy"
      else
        rm -f "$SITES/baruchstreks.caddy"
      fi
      docker exec "$caddy" caddy reload --config /etc/caddy/Caddyfile </dev/null >/dev/null 2>&1 || true
      exit 1
    fi
  fi

  # 7. Remove old tags of OUR image only; keep the current and the previous one for a quick rollback.
  echo "==> cleaning old $IMAGE_REPO tags"
  local ref
  while IFS= read -r ref; do
    [ -n "$ref" ] || continue
    case "$ref" in "$IMAGE_REPO":*) ;; *) continue ;; esac
    [ "$ref" = "$image_ref" ] && continue
    [ -n "$previous" ] && [ "$ref" = "$previous" ] && continue
    docker rmi "$ref" </dev/null >/dev/null 2>&1 || true
  done < <(docker images "$IMAGE_REPO" --format '{{.Repository}}:{{.Tag}}' </dev/null | grep -v ':<none>$' || true)

  echo "deployed $image_ref"
}

main "$@"
