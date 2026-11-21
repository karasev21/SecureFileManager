# Runs the Docker-isolated local CI pipeline using nektos/act.
# Invoke from the repository root:
#   powershell -ExecutionPolicy Bypass -File .github/scripts/run-local-ci.ps1
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $repoRoot

foreach ($command in @('docker', 'act')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Missing '$command'. Install it and reopen your terminal."
    }
}

& docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker daemon is not available. Start Docker Desktop (Linux containers) first.'
}

# Build the runner job images explicitly so the .NET/Python containers contain
# Node.js 24, required by GitHub's JavaScript actions.
& docker build -f .github/docker/act-dotnet.Dockerfile -t securefilemanager/act-dotnet:local .
if ($LASTEXITCODE -ne 0) { throw 'Could not build the .NET local runner image.' }
& docker build -f .github/docker/act-semgrep.Dockerfile -t securefilemanager/act-semgrep:local .
if ($LASTEXITCODE -ne 0) { throw 'Could not build the Semgrep local runner image.' }

$artifactDir = Join-Path $repoRoot '.act-artifacts'
$cacheDir = Join-Path $repoRoot '.act-cache'
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
Write-Host "Local CI: Build -> Test / Semgrep / Trivy -> Deploy" -ForegroundColor Cyan
Write-Host "Artifacts: $artifactDir"
Write-Host "Docker socket is shared with local runners: run only trusted workflows."

& act workflow_dispatch `
    -W .github/workflows/local-ci.yml `
    -P 'ubuntu-latest=catthehacker/ubuntu:act-latest' `
    --container-architecture linux/amd64 `
    --pull=false `
    --bind

if ($LASTEXITCODE -ne 0) {
    throw "Local CI failed (act exit code $LASTEXITCODE). Inspect the failing job above."
}
Write-Host 'All local CI jobs completed successfully.' -ForegroundColor Green
