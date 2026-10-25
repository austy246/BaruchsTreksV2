#Requires -Version 7.2
<#
.SYNOPSIS
  Deploys Baruch's Treks to the shared VPS from this machine: build + push to GHCR, upload deploy files, run
  remote-deploy.sh over SSH, check /healthz.

.DESCRIPTION
  Without -Tag: refuses to run with tracked changes (outside .claude/) or untracked files under the build inputs
  (src/, Directory.Build.props, global.json, BaruchsTreks.slnx), tags the image with the full SHA of HEAD,
  runs `dotnet test`, builds linux/amd64 and pushes <sha> and latest (needs `docker login ghcr.io` beforehand).
  With -Tag: builds nothing, only checks that the image exists in GHCR. Use it for a rollback; it also works for a
  Django-era commit (no src/BaruchsTreks project in that commit, so no --selftest; docker-compose.yml and the Caddy
  block come from that commit, which restores port 8000 and the volume; the old Django variables stay in .env
  until the owner approves their removal).
  The deploy files (deploy/) are always uploaded from the commit of the tag; remote-deploy.sh itself is
  streamed from the CURRENT working copy over SSH stdin.
  GHCR login (once; the token needs write:packages):  gh auth token | docker login ghcr.io -u austy246 --password-stdin
  SSH uses ssh's own defaults (ssh-agent, ~/.ssh/config); there is deliberately no option to pass a key file.
  Do not run it while the GitHub workflow is deploying.

.PARAMETER PrepareOnly   Only migrate deploy/.env on the server and run the read-only `--check-data`; deploys nothing.
.PARAMETER UpdateSecrets Pipe deploy/secrets.env (gitignored, KEY=VALUE lines) into update-env-secrets.sh on the server.
.PARAMETER DryRun        Print the commands, change nothing and call neither SSH nor the registry.
#>
[CmdletBinding()]
param(
    [string]$Tag,
    [switch]$PrepareOnly,
    [switch]$UpdateSecrets,
    [switch]$DryRun,
    [string]$SshTarget = "deploy@57.131.165.173",
    [string]$Domain = "treks.austy.cz"
)

$ErrorActionPreference = 'Stop'
$ImageRepo = 'ghcr.io/austy246/baruchstreks-app'
$RemoteDir = '/srv/baruchstreks'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

# Runs (or, with -DryRun, only prints) one external command line.
function Invoke-Native([string]$Display, [scriptblock]$Action) {
    if ($DryRun) { Write-Host "[dry-run] $Display"; return }
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $Display" }
}

# Binary-safe pipes and redirects: PowerShell pipes re-encode bytes, cmd.exe does not.
function Invoke-Cmd([string]$Line) {
    Invoke-Native $Line { cmd /c $Line }
}

Push-Location $repoRoot
try {
    # --- 0. Which image -----------------------------------------------------------------------------------
    $built = $false
    if ([string]::IsNullOrWhiteSpace($Tag)) {
        # The image is built from the working tree but tagged with the SHA of HEAD, so anything that could end up
        # in the build and is not in HEAD must block: tracked changes, and untracked (non-ignored) build inputs.
        $dirty = @(git status --porcelain --untracked-files=no -- . ':(exclude).claude')
        $dirty += @(git ls-files --others --exclude-standard -- src Directory.Build.props global.json BaruchsTreks.slnx |
            ForEach-Object { "?? $_" })
        if ($dirty.Count -gt 0) {
            $msg = "Uncommitted changes (tracked, or untracked under the build inputs; commit, stash or remove first):`n" + ($dirty -join "`n")
            if ($DryRun) { Write-Warning $msg } else { throw $msg }
        }
        $Tag = (git rev-parse HEAD).Trim()
        $built = $true
    }
    elseif ($Tag -match '^[0-9a-f]{7,39}$') {
        $Tag = (git rev-parse --verify "$Tag^{commit}").Trim()   # image tags are full SHAs
    }
    if ($Tag -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$') { throw "Invalid tag '$Tag'." }
    $commit = (git rev-parse --verify --quiet "$Tag^{commit}" 2>$null)
    if (-not $commit) { throw "'$Tag' is not a commit of this repository; it is needed to upload deploy/ from that commit." }
    $commit = $commit.Trim()
    $image = "${ImageRepo}:$Tag"
    Write-Host "Image: $image  (commit $commit)"

    $ssh = "ssh $SshTarget"   # never -i: key selection is up to ssh (agent, ~/.ssh/config)

    $remoteScript = Join-Path $repoRoot 'deploy\remote-deploy.sh'
    if ([System.IO.File]::ReadAllBytes($remoteScript) -contains 13) {
        throw "deploy\remote-deploy.sh has CRLF line endings; bash on the server needs LF (check .gitattributes, re-checkout the file)."
    }

    $secrets = Join-Path $repoRoot 'deploy\secrets.env'
    if ($UpdateSecrets -and -not $DryRun -and -not (Test-Path -LiteralPath $secrets)) { throw "deploy\secrets.env not found (gitignored KEY=VALUE file)." }

    # Remote arguments are decided here, before anything touches the server. git ls-tree writes nothing to
    # stderr when the path is missing (Django-era commits), unlike `git cat-file -e`.
    $flags = @()
    $hasCsproj = [bool](git ls-tree --name-only $commit -- src/BaruchsTreks/BaruchsTreks.csproj)
    if ($hasCsproj) { $flags += '--selftest' }   # Django-era images have no --selftest
    if ($PrepareOnly) { $flags += '--prepare-only' }
    $remoteArgs = (@($image) + $flags) -join ' '

    # --- 1. Test, build, push -----------------------------------------------------------------------------
    if ($built) {
        Write-Step "dotnet test"
        Invoke-Native "dotnet test" { dotnet test }
        Write-Step "docker buildx build + push"
        $latest = "${ImageRepo}:latest"
        Invoke-Native "docker buildx build --platform linux/amd64 -f src/BaruchsTreks/Dockerfile -t $image -t $latest --push ." {
            docker buildx build --platform linux/amd64 -f src/BaruchsTreks/Dockerfile -t $image -t $latest --push .
        }
    }
    else {
        Write-Step "Checking that $image exists in the registry"
        Invoke-Native "docker manifest inspect $image" { docker manifest inspect $image | Out-Null }
    }

    # --- 2. Deploy files from the commit of the tag -----------------------------------------------------------
    Write-Step "Uploading deploy/ from $commit"
    Invoke-Cmd "git archive --format=tar $commit deploy | $ssh tar -x -C $RemoteDir"

    # --- 3. Secrets (optional) ---------------------------------------------------------------------------
    if ($UpdateSecrets) {
        Write-Step "Updating secrets on the server (names only are printed)"
        Invoke-Cmd "$ssh bash $RemoteDir/deploy/update-env-secrets.sh < deploy\secrets.env"
    }

    # --- 4. remote-deploy.sh from the working copy ---------------------------------------------------------
    Write-Step "remote-deploy.sh $remoteArgs"
    Invoke-Cmd "$ssh bash -s -- $remoteArgs < deploy\remote-deploy.sh"

    # --- 5. Health check ----------------------------------------------------------------------------------
    if ($PrepareOnly) {
        Write-Host "Prepare-only finished: nothing was deployed." -ForegroundColor Green
    }
    else {
        $url = "https://$Domain/healthz"
        Write-Step "GET $url (5 attempts, 3 s apart)"
        if ($DryRun) { Write-Host "[dry-run] Invoke-WebRequest $url" }
        else {
            $healthy = $false
            for ($i = 1; $i -le 5 -and -not $healthy; $i++) {
                try {
                    $healthy = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10).StatusCode -eq 200
                }
                catch { Write-Host "attempt ${i}: $($_.Exception.Message)" }
                if (-not $healthy -and $i -lt 5) { Start-Sleep -Seconds 3 }
            }
            if (-not $healthy) { throw "$url did not answer 200 after 5 attempts." }
            Write-Host "Deployed $image, $url is healthy." -ForegroundColor Green
        }
    }
}
finally {
    Pop-Location
}
