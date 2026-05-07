<#
.SYNOPSIS
  Create the Laplace development database and install the laplace_pg
  extension. The extension creates the canonical schema. Idempotent: safe
  to re-run.

  Pre-1.0 the substrate has ONE canonical schema, defined by the extension
  (ext/laplace_pg/sql/laplace_pg--0.1.0.sql). There are no migrations.
  Schema refactors are made in place by editing the extension SQL and
  reinstalling the extension.

.PARAMETER ConnectionString
  Postgres admin connection (must have CREATE DATABASE + CREATE EXTENSION).
  Default reads $env:LAPLACE_DB_ADMIN, falling back to local default.

.PARAMETER DatabaseName
  Default: laplace_dev
#>

[CmdletBinding()]
param(
  [string]$ConnectionString = $(if ($env:LAPLACE_DB_ADMIN) { $env:LAPLACE_DB_ADMIN } else { 'host=localhost port=5432 user=postgres dbname=postgres' }),
  [string]$DatabaseName = 'laplace_dev'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Invoke-Psql {
  param([string]$Conn, [string]$Sql)
  & psql --quiet --no-psqlrc --no-align --tuples-only --command $Sql $Conn
}

Write-Host "Laplace DB bootstrap" -ForegroundColor Cyan
Write-Host "  Database: $DatabaseName"

# Step 1: ensure database exists
Write-Host "[1/2] Ensure database exists..."
$exists = & psql --quiet --no-psqlrc --no-align --tuples-only `
  --command "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'" `
  $ConnectionString
if (-not $exists) {
  Invoke-Psql $ConnectionString "CREATE DATABASE $DatabaseName"
  Write-Host "  Created database $DatabaseName" -ForegroundColor Green
} else {
  Write-Host "  Database exists" -ForegroundColor Green
}

# Step 2: install extension (which creates the canonical schema)
Write-Host "[2/2] Install laplace_pg extension (creates schema)..."
$dbConn = $ConnectionString -replace 'dbname=\w+', "dbname=$DatabaseName"
Invoke-Psql $dbConn "CREATE EXTENSION IF NOT EXISTS laplace_pg"
Write-Host "  Extension installed" -ForegroundColor Green

Write-Host ""
Write-Host "Bootstrap complete." -ForegroundColor Cyan
