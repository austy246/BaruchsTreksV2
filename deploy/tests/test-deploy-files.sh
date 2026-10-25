#!/usr/bin/env bash
# Static checks of the deploy files. Run: bash deploy/tests/test-deploy-files.sh
#   - no host-wide Docker cleanup (the VPS is shared; the old CI did that) anywhere in the deploy tooling,
#   - deploy/docker-compose.yml and the Caddy block have the keys production needs,
#   - the workflow keeps its structure (parallel test/build, deploy needs both, :latest last, secrets over stdin),
#   - shell scripts have LF endings.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
compose="$root/deploy/docker-compose.yml"
caddy="$root/deploy/baruchstreks.caddy"
workflow="$root/.github/workflows/deploy.yml"
remote="$root/deploy/remote-deploy.sh"

fail=0
ok()   { echo "ok   - $1"; }
bad()  { echo "FAIL - $1"; fail=1; }
check() { local desc="$1"; shift; if "$@"; then ok "$desc"; else bad "$desc"; fi; }
has()  { grep -Eq -- "$2" "$1"; }
lacks() { ! grep -Eq -- "$2" "$1"; }

# --- files that are shipped to / run on the server or in CI (this test and the other tests are excluded:
#     they have to mention the forbidden strings to look for them) ---
shipped=("$root"/deploy/*.sh "$root"/deploy/deploy.ps1 "$compose" "$caddy" "$workflow")
for f in "${shipped[@]}"; do check "exists: ${f#"$root"/}" test -f "$f"; done

for f in "${shipped[@]}"; do
  rel="${f#"$root"/}"
  check "$rel: no 'docker image prune'" lacks "$f" 'image[[:space:]]+prune'
  check "$rel: no 'docker system prune'" lacks "$f" 'system[[:space:]]+prune'
  check "$rel: no other host-wide prune (container/volume/builder/network)" lacks "$f" '(container|volume|builder|network)[[:space:]]+prune'
  check "$rel: no 'prune' at all" lacks "$f" 'prune'
done

# cleanup is explicit: removes our own old tags only
check "remote-deploy.sh removes images with docker rmi" has "$remote" 'docker rmi'
check "remote-deploy.sh cleanup is limited to the baruchstreks-app repo" has "$remote" 'IMAGE_REPO="ghcr.io/austy246/baruchstreks-app"'
check "remote-deploy.sh skips refs outside our repo" has "$remote" 'case "\$ref" in "\$IMAGE_REPO":\*\) ;; \*\) continue ;; esac'
check "remote-deploy.sh rolls back to the previous image" has "$remote" 'rolling back to'
check "remote-deploy.sh uses compose up --wait" has "$remote" 'docker compose -p "\$PROJECT" up -d --wait'
check "remote-deploy.sh validates caddy before reload" has "$remote" 'caddy validate'
check "remote-deploy.sh reloads caddy only for a changed block" has "$remote" 'cmp -s "\$rendered"'
check "remote-deploy.sh restores the block when validation fails" has "$remote" 'restoring the previous file'
check "remote-deploy.sh uses baruchstreks.caddy" has "$remote" 'baruchstreks\.caddy'

# --- compose ---
check "compose: project name baruchstreks" has "$compose" '^name: baruchstreks$'
check "compose: no volumes" lacks "$compose" '(^|[[:space:]])volumes:'
check "compose: AllowedHosts from a required APP_DOMAIN plus localhost" has "$compose" 'AllowedHosts: \$\{APP_DOMAIN:\?[^}]+\};localhost'
check "compose: forwarded headers enabled" has "$compose" 'ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"'
check "compose: ASPNETCORE_ENVIRONMENT Production" has "$compose" 'ASPNETCORE_ENVIRONMENT: Production'
check "compose: healthcheck via dotnet BaruchsTreks.dll --healthcheck" has "$compose" '"dotnet", "BaruchsTreks.dll", "--healthcheck"'
check "compose: healthcheck timeout 15s" has "$compose" 'timeout: 15s'
check "compose: healthcheck start_period" has "$compose" 'start_period:'
check "compose: healthcheck start_interval" has "$compose" 'start_interval:'
check "compose: cpu_shares 256" has "$compose" 'cpu_shares: 256'
check "compose: oom_score_adj 500" has "$compose" 'oom_score_adj: 500'
check "compose: mem_limit 512m" has "$compose" 'mem_limit: 512m'
check "compose: logging json-file with limits" has "$compose" 'driver: json-file'
check "compose: log size limit" has "$compose" 'max-size:'
check "compose: env_file .env" has "$compose" 'env_file: \.env'
check "compose: external network web" has "$compose" 'external: true'
check "compose: alias baruchstreks-app" has "$compose" 'aliases: \[baruchstreks-app\]'
check "compose: image comes from APP_IMAGE" has "$compose" 'image: \$\{APP_IMAGE:\?'
check "compose: nothing from the Django app left (port 8000 / python)" lacks "$compose" '8000|python|app_data'

# --- caddy ---
check "caddy: upstream baruchstreks-app:8080" has "$caddy" 'reverse_proxy baruchstreks-app:8080'
check "caddy: body limit 50MB" has "$caddy" 'max_size 50MB'
check "caddy: domain placeholder" has "$caddy" '^__APP_DOMAIN__ \{'
for h in Strict-Transport-Security X-Content-Type-Options Referrer-Policy X-Frame-Options; do
  check "caddy: header $h" has "$caddy" "$h"
done
check "caddy: Server header removed" has "$caddy" '^[[:space:]]+-Server$'
check "caddy: no Django port" lacks "$caddy" ':8000'

# --- workflow ---
check "workflow: push to main" has "$workflow" 'branches: \[main\]'
check "workflow: docs-only changes ignored" has "$workflow" "paths-ignore: \['docs/\*\*', '\*\*/\*\.md'"
check "workflow: test job" has "$workflow" '^  test:'
check "workflow: build job" has "$workflow" '^  build:'
check "workflow: deploy needs test and build" has "$workflow" 'needs: \[test, build\]'
check "workflow: build does not wait for test (parallel)" bash -c '! sed -n "/^  build:/,/^  deploy:/p" "$1" | grep -q "needs:"' _ "$workflow"
check "workflow: test does not wait for build (parallel)" bash -c '! sed -n "/^  test:/,/^  build:/p" "$1" | grep -q "needs:"' _ "$workflow"
check "workflow: GHCR login with GITHUB_TOKEN" has "$workflow" 'password: \$\{\{ secrets\.GITHUB_TOKEN \}\}'
check "workflow: image tag is the full SHA" has "$workflow" 'tags: \$\{\{ env\.IMAGE \}\}:\$\{\{ github\.sha \}\}'
check "workflow: image repo" has "$workflow" 'IMAGE: ghcr\.io/austy246/baruchstreks-app'
check "workflow: Dockerfile path" has "$workflow" 'file: src/BaruchsTreks/Dockerfile'
check "workflow: secrets go through update-env-secrets.sh" has "$workflow" '\| ssh vps bash /srv/baruchstreks/deploy/update-env-secrets\.sh'
check "workflow: remote-deploy.sh is streamed over stdin" has "$workflow" 'ssh vps bash -s -- .*< deploy/remote-deploy\.sh'
check "workflow: secrets are only referenced as env/with values, never inline in a command" bash -c '! grep -E "\$\{\{ *secrets\." "$1" | grep -Ev "^[[:space:]]+[A-Za-z_0-9]+: \$\{\{ secrets\.[A-Z_0-9]+ \}\}$|^[[:space:]]*#"  | grep -q .' _ "$workflow"
check "workflow: no ssh -i" lacks "$workflow" 'ssh -i'
check "workflow: no secret variable echoed" lacks "$workflow" 'echo[[:space:]]+"?\$\{?[A-Za-z_0-9]*(SECRET|PASSWORD|_KEY|CONNECTION|STORAGE)'
check "workflow: latest tagged from the sha image" has "$workflow" 'imagetools create -t "\$IMAGE:latest" "\$IMAGE:\$GITHUB_SHA"'
check "workflow: :latest is created in exactly one place" bash -c '[ "$(grep -c "IMAGE:latest" "$1")" = 1 ]' _ "$workflow"
check "workflow: latest comes after the deploy step" bash -c '[ "$(grep -n "Deploy (selftest" "$1" | cut -d: -f1)" -lt "$(grep -n "imagetools create" "$1" | cut -d: -f1)" ]' _ "$workflow"
check "workflow: health check before latest" bash -c '[ "$(grep -n "name: Health check" "$1" | cut -d: -f1)" -lt "$(grep -n "imagetools create" "$1" | cut -d: -f1)" ]' _ "$workflow"
check "workflow: ssh key removed at the end" has "$workflow" 'rm -f ~/\.ssh/id_deploy'

# --- no -i in the local script either ---
check "deploy.ps1: ssh without -i" lacks "$root/deploy/deploy.ps1" 'ssh -i|-SshKey'
check "deploy.ps1: build pushes only the sha tag" bash -c '! grep -E "buildx build.* -t [^ ]*latest" "$1"' _ "$root/deploy/deploy.ps1"
check "deploy.ps1: latest moved with imagetools after the health check" bash -c '[ "$(grep -n "is healthy" "$1" | cut -d: -f1 | head -1)" -lt "$(grep -n "imagetools create" "$1" | cut -d: -f1 | head -1)" ]' _ "$root/deploy/deploy.ps1"
check "remote-deploy.sh: every compose call pins the project" bash -c '! grep -E "docker compose " "$1" | grep -Ev "^[[:space:]]*(#|echo)" | grep -v -- "-p \"\$PROJECT\"" | grep -q .' _ "$remote"
check "deploy.ps1: GHCR login hint" has "$root/deploy/deploy.ps1" 'gh auth token \| docker login ghcr\.io -u austy246 --password-stdin'
check "deploy.ps1: -Tag parameter" has "$root/deploy/deploy.ps1" '\[string\]\$Tag'
check "deploy.ps1: files taken from the deployed commit (git archive)" has "$root/deploy/deploy.ps1" 'git archive --format=tar \$commit deploy'
check "deploy.ps1: remote logic over ssh stdin" has "$root/deploy/deploy.ps1" 'bash -s -- \$remoteArgs < deploy.remote-deploy\.sh'
check "deploy.ps1: refuses a dirty tree" has "$root/deploy/deploy.ps1" 'Uncommitted changes'
check "deploy.ps1: dotnet test before building" has "$root/deploy/deploy.ps1" 'dotnet test'
check "deploy.ps1: buildx linux/amd64 push" has "$root/deploy/deploy.ps1" 'buildx build --platform linux/amd64'
check "deploy.ps1: no leftovers of the reference project" lacks "$root/deploy/deploy.ps1" -i'toiletlabels'

# --- line endings: no CR in anything bash or the server reads ---
for f in "$root"/deploy/*.sh "$root"/deploy/tests/*.sh "$caddy" "$compose"; do
  check "LF endings: ${f#"$root"/}" bash -c '! grep -q $'"'"'\r'"'"' "$1"' _ "$f"
done

# --- docker compose config (when docker is available) ---
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  rendered="$(mktemp)"
  tmpdir="$(mktemp -d)"
  cp "$compose" "$tmpdir/docker-compose.yml"
  printf 'APP_IMAGE=ghcr.io/austy246/baruchstreks-app:testsha\nAPP_DOMAIN=treks.example.test\nSOME_SECRET=dummy\n' > "$tmpdir/.env"
  docker compose -f "$tmpdir/docker-compose.yml" --env-file "$tmpdir/.env" config > "$rendered" 2>&1
  check "docker compose config is valid" test $? -eq 0
  check "rendered: AllowedHosts is the domain plus localhost" has "$rendered" 'AllowedHosts: treks\.example\.test;localhost'
  check "rendered: image is APP_IMAGE" has "$rendered" 'image: ghcr\.io/austy246/baruchstreks-app:testsha'
  rm -rf "$rendered" "$tmpdir"
else
  echo "skip - docker compose config (docker not available)"
fi

if [ "$fail" -eq 0 ]; then echo "ALL PASSED"; else echo "SOME TESTS FAILED"; exit 1; fi
