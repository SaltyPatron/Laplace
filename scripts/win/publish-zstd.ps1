#requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$Python = $env:LAPLACE_CODEGEN_PYTHON
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/machine-env.ps1"
if ([string]::IsNullOrWhiteSpace($Python) -and $env:LAPLACE_ENGINE_BUILD) {
    $cache = Join-Path $env:LAPLACE_ENGINE_BUILD 'CMakeCache.txt'
    if (Test-Path -LiteralPath $cache -PathType Leaf) {
        foreach ($line in [IO.File]::ReadLines($cache)) {
            if ($line -match '^LAPLACE_CODEGEN_PYTHON:[^=]+=(.+)$') {
                $Python = $Matches[1]
                break
            }
        }
    }
}
$pythonName = if ([string]::IsNullOrWhiteSpace($Python)) { 'python' } else { $Python }
$python = (Get-Command $pythonName -CommandType Application -ErrorAction Stop).Source
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$probe = Join-Path $repo 'scripts\check-zstd-runtime.py'
$release = Get-Content -Raw (Join-Path $repo 'deploy\zstd-release.json') | ConvertFrom-Json
$version = $release.version
$library = $env:LAPLACE_ZSTD_LIBRARY
if (-not $library) {
    $apiEnvironment = Join-Path $repo 'deploy\windows\laplace-api.env'
    if (-not (Test-Path -LiteralPath $apiEnvironment)) { $apiEnvironment += '.example' }
    foreach ($file in @($apiEnvironment, (Join-Path $repo 'deploy\secrets\chess-lab.env'))) {
        if (-not (Test-Path -LiteralPath $file)) { continue }
        foreach ($line in Get-Content -LiteralPath $file) {
            if ($line -match '^\s*LAPLACE_ZSTD_LIBRARY\s*=(.*)$') {
                $library = $Matches[1].Trim().Trim('"').Trim("'")
                break
            }
        }
        if ($library) { break }
    }
}
if (-not $library) {
    $prefix = $env:LAPLACE_DEPENDENCY_ZSTD_PREFIX
    if (-not $prefix) { $prefix = Join-Path $env:LAPLACE_DEPS "zstd\$version" }
    $library = Join-Path $prefix 'bin\zstd.dll'
}
if (-not [IO.Path]::IsPathFullyQualified($library) -or -not (Test-Path -LiteralPath $library -PathType Leaf)) {
    throw 'LAPLACE_ZSTD_LIBRARY must name an existing absolute shared-library path'
}
& $python $probe --library $library --require-version $version
if ($LASTEXITCODE -ne 0) { throw 'Configured native Zstandard library failed its PGN decode check' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$target = Join-Path $Destination 'libzstd.dll'
Copy-Item -LiteralPath $library -Destination $target -Force
if ((Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
    throw 'Published Zstandard DLL does not match the selected dependency'
}
& $python $probe --library $target --require-version $version
if ($LASTEXITCODE -ne 0) { throw 'Published native Zstandard library failed its PGN decode check' }
$licenses = Join-Path $Destination 'third-party-licenses\zstd'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
$licenseRoot = $env:LAPLACE_ZSTD_SOURCE
if (-not $licenseRoot) { $licenseRoot = Join-Path $env:LAPLACE_EXTERNAL 'zstd' }
foreach ($name in @('LICENSE', 'COPYING')) {
    $source = Join-Path $licenseRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Declared Zstandard source is missing $name" }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $release.license_sha256.$name) {
        throw "Zstandard $name does not match the locked release"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenses $name) -Force
}
