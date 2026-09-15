#requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$probe = Join-Path $repo 'scripts\check-zstd-runtime.py'
$version = (Get-Content -Raw (Join-Path $repo 'deploy\zstd-release.json') | ConvertFrom-Json).version
$builtLibrary = & python (Join-Path $repo 'scripts\install-zstd.py') --print-path
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the Zstandard library and license closure built from the locked source' }
$builtLibrary = "$builtLibrary".Trim()
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
    $library = $builtLibrary
}
if (-not [IO.Path]::IsPathFullyQualified($library) -or -not (Test-Path -LiteralPath $library -PathType Leaf)) {
    throw 'LAPLACE_ZSTD_LIBRARY must name an existing absolute shared-library path'
}
& python $probe --library $library --require-version $version
if ($LASTEXITCODE -ne 0) { throw 'Configured native Zstandard library failed its PGN decode check' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$target = Join-Path $Destination 'libzstd.dll'
Copy-Item -LiteralPath $library -Destination $target -Force
if ((Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
    throw 'Published Zstandard DLL does not match the selected source build'
}
& python $probe --library $target --require-version $version
if ($LASTEXITCODE -ne 0) { throw 'Published native Zstandard library failed its PGN decode check' }
$licenses = Join-Path $Destination 'third-party-licenses\zstd'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
foreach ($name in @('LICENSE', 'COPYING')) {
    $source = Join-Path (Split-Path -Parent $builtLibrary) $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Zstandard source build is missing $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenses $name) -Force
}
