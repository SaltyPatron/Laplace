<#
.SYNOPSIS
  Run the pgTAP suite against the laplace_dev database.
#>

[CmdletBinding()]
param(
  [string]$ConnectionString = $(if ($env:LAPLACE_DB) { $env:LAPLACE_DB } else { 'host=localhost port=5432 user=postgres dbname=laplace_dev' })
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$tests = Get-ChildItem $here -Filter '*.sql' | Sort-Object Name

if ($tests.Count -eq 0) {
  Write-Warning "No pgTAP test files found in $here"
  exit 0
}

$failed = 0
foreach ($t in $tests) {
  Write-Host "Running $($t.Name)..." -ForegroundColor Yellow
  & psql --quiet --no-psqlrc --file $t.FullName $ConnectionString
  if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAILED: $($t.Name)" -ForegroundColor Red
    $failed++
  } else {
    Write-Host "  OK: $($t.Name)" -ForegroundColor Green
  }
}

if ($failed -gt 0) {
  Write-Host "$failed pgTAP file(s) failed." -ForegroundColor Red
  exit 1
}
Write-Host "All pgTAP tests passed." -ForegroundColor Green
