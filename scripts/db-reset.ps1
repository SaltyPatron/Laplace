<#
.SYNOPSIS
  Drop + recreate + bootstrap the Laplace development database.

.DESCRIPTION
  DESTRUCTIVE. Requires -Force to actually run. Use only for development.
  Production databases are migrated forward, never reset.
#>

[CmdletBinding()]
param(
  [string]$ConnectionString = $(if ($env:LAPLACE_DB_ADMIN) { $env:LAPLACE_DB_ADMIN } else { 'host=localhost port=5432 user=postgres dbname=postgres' }),
  [string]$DatabaseName = 'laplace_dev',
  [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not $Force) {
  Write-Error "db-reset is destructive. Re-run with -Force to confirm."
  exit 1
}

Write-Host "Dropping database $DatabaseName ..." -ForegroundColor Yellow
& psql --quiet --no-psqlrc --command "DROP DATABASE IF EXISTS $DatabaseName" $ConnectionString
if ($LASTEXITCODE -ne 0) { throw "Drop failed" }

& (Join-Path $PSScriptRoot 'db-bootstrap.ps1') -ConnectionString $ConnectionString -DatabaseName $DatabaseName
