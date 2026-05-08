#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build the laplace-pg Docker image stack in dependency order.

.DESCRIPTION
  Reads docker/versions.env for canonical version pins, then builds:
    1. ${IMG_NS}/postgres:${POSTGRES_VERSION}
    2. ${IMG_NS}/postgis:${POSTGIS_VERSION}
    3. ${IMG_NS}/pgext:dev
    4. laplace-pg:latest

  Each layer is a separately tagged image. Bumping a version in versions.env
  invalidates only that layer + downstream layers.

  Requires git submodules at external/postgres, external/postgis, external/proj,
  external/geos checked out at tags matching versions.env (run
  `git submodule update --init --recursive` after cloning).

.PARAMETER Layer
  Build only one layer (postgres|postgis|pgext|final|all). Defaults to all.

.PARAMETER NoCache
  Pass --no-cache to docker build. Use only when intentionally rebuilding.
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'postgres', 'postgis', 'pgext', 'final')]
    [string]$Layer = 'all',
    [switch]$NoCache
)

$ErrorActionPreference = 'Stop'

# repo root = parent of scripts/docker/
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $repoRoot

# ---- read versions.env into a hashtable ----
$versionsFile = Join-Path $repoRoot 'docker/versions.env'
if (-not (Test-Path $versionsFile)) {
    throw "versions.env not found at $versionsFile"
}
$versions = @{}
Get-Content $versionsFile | ForEach-Object {
    if ($_ -match '^\s*#') { return }
    if ($_ -match '^\s*([A-Z_]+)\s*=\s*(.+?)\s*$') {
        $versions[$Matches[1]] = $Matches[2]
    }
}

$ns       = $versions.IMG_NS
$pgVer    = $versions.POSTGRES_VERSION
$gisVer   = $versions.POSTGIS_VERSION
$projVer  = $versions.PROJ_VERSION
$geosVer  = $versions.GEOS_VERSION
$hpckit   = $versions.ONEAPI_HPCKIT
$runtime  = $versions.ONEAPI_RUNTIME

# ---- check submodules are present ----
$requiredSubmodules = @('external/postgres', 'external/postgis', 'external/proj', 'external/geos')
foreach ($sm in $requiredSubmodules) {
    $smPath = Join-Path $repoRoot $sm
    if (-not (Test-Path "$smPath/.git") -and -not (Test-Path "$smPath/configure") -and -not (Test-Path "$smPath/CMakeLists.txt")) {
        throw "Submodule '$sm' missing or empty. Run: git submodule update --init --recursive"
    }
}

function Build-Layer {
    param([string]$Name, [string]$Dockerfile, [string]$Tag, [string[]]$ExtraArgs)
    Write-Host "==== Building $Name -> $Tag ====" -ForegroundColor Cyan
    $args = @(
        'build',
        '--progress=plain',
        '-f', $Dockerfile,
        '-t', $Tag
    )
    if ($NoCache) { $args += '--no-cache' }
    if ($ExtraArgs) { $args += $ExtraArgs }
    $args += '.'
    & docker @args
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Name" }
}

if ($Layer -in @('all', 'postgres')) {
    Build-Layer 'postgres' 'docker/postgres.Dockerfile' "$ns/postgres:$pgVer" @(
        '--build-arg', "ONEAPI_HPCKIT=$hpckit",
        '--build-arg', "ONEAPI_RUNTIME=$runtime",
        '--build-arg', "POSTGRES_VERSION=$pgVer"
    )
    docker tag "$ns/postgres:$pgVer" "$ns/postgres:latest"
}

if ($Layer -in @('all', 'postgis')) {
    Build-Layer 'postgis' 'docker/postgis.Dockerfile' "$ns/postgis:$gisVer" @(
        '--build-arg', "ONEAPI_HPCKIT=$hpckit",
        '--build-arg', "IMG_NS=$ns",
        '--build-arg', "POSTGRES_VERSION=$pgVer",
        '--build-arg', "POSTGIS_VERSION=$gisVer",
        '--build-arg', "PROJ_VERSION=$projVer",
        '--build-arg', "GEOS_VERSION=$geosVer"
    )
    docker tag "$ns/postgis:$gisVer" "$ns/postgis:latest"
}

if ($Layer -in @('all', 'pgext')) {
    Build-Layer 'pgext' 'docker/pgext.Dockerfile' "$ns/pgext:dev" @(
        '--build-arg', "ONEAPI_HPCKIT=$hpckit",
        '--build-arg', "IMG_NS=$ns",
        '--build-arg', "POSTGIS_VERSION=$gisVer"
    )
}

if ($Layer -in @('all', 'final')) {
    Build-Layer 'final' 'docker/final.Dockerfile' 'laplace-pg:latest' @(
        '--build-arg', "IMG_NS=$ns"
    )
}

Write-Host "==== Build complete ====" -ForegroundColor Green
docker images | Select-String -Pattern '^(laplace-pg|laplace/)'
