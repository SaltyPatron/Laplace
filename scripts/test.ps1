<#
.SYNOPSIS
  Run all Laplace test suites: native CTest + managed xUnit + SQL pgTAP.

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER Suite
  native | managed | sql | all (default: all)
#>

[CmdletBinding()]
param(
  [ValidateSet('Release','Debug')] [string]$Configuration = 'Release',
  [ValidateSet('native','managed','sql','all')] [string]$Suite = 'all'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repoRoot    = Resolve-Path (Join-Path $PSScriptRoot '..')
$nativeBuild = Join-Path $repoRoot 'ext\laplace_pg\build'

$failures = @()

if ($Suite -in @('native','all')) {
  Write-Host "Native tests (CTest)" -ForegroundColor Yellow
  if (-not (Test-Path $nativeBuild)) {
    Write-Warning "Native build directory missing — run scripts/build.ps1 first."
    $failures += 'native (build missing)'
  } else {
    Push-Location $nativeBuild
    try {
      & ctest --build-config $Configuration --output-on-failure
      if ($LASTEXITCODE -ne 0) { $failures += 'native' }
    } finally { Pop-Location }
  }
  Write-Host ""
}

if ($Suite -in @('managed','all')) {
  Write-Host "Managed tests (xUnit)" -ForegroundColor Yellow
  Push-Location $repoRoot
  try {
    & dotnet test Laplace.slnx --configuration $Configuration --nologo --no-build
    if ($LASTEXITCODE -ne 0) { $failures += 'managed' }
  } finally { Pop-Location }
  Write-Host ""
}

if ($Suite -in @('sql','all')) {
  Write-Host "SQL tests (pgTAP)" -ForegroundColor Yellow
  $pgtapRunner = Join-Path $repoRoot 'tests\sql\run_pgtap.ps1'
  if (Test-Path $pgtapRunner) {
    & $pgtapRunner
    if ($LASTEXITCODE -ne 0) { $failures += 'sql' }
  } else {
    Write-Warning "pgTAP runner not yet present — Phase 2 deliverable."
  }
  Write-Host ""
}

if ($failures.Count -eq 0) {
  Write-Host "All test suites passed." -ForegroundColor Green
  exit 0
} else {
  Write-Host "Failed suites: $($failures -join ', ')" -ForegroundColor Red
  exit 1
}
