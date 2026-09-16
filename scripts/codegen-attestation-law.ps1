param(
    [string]$Root = (Resolve-Path "$PSScriptRoot\.."),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$py = Join-Path $Root 'scripts/codegen-attestation-law.py'
if (-not (Test-Path $py)) { throw "missing $py" }

$stampDir = Join-Path $Root 'build/.stamps'
$stamp = Join-Path $stampDir 'attestation-law'
$manifest = @(
    (Get-Item (Join-Path $Root 'engine/manifest/relation_types.toml')).LastWriteTimeUtc.Ticks
    (Get-Item (Join-Path $Root 'engine/manifest/pos_tags.toml')).LastWriteTimeUtc.Ticks
    (Get-Item $py).LastWriteTimeUtc.Ticks
) -join ':'

if (-not $Force -and (Test-Path -LiteralPath $stamp)) {
    $prev = (Get-Content -LiteralPath $stamp -Raw -ErrorAction SilentlyContinue)
    if ($prev -eq $manifest) {
        Write-Host "attestation law codegen skipped (stamp fresh)"
        exit 0
    }
}

python $py
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force -Path $stampDir | Out-Null
Set-Content -LiteralPath $stamp -Value $manifest -NoNewline -Encoding ascii
Write-Host "attestation law codegen complete"