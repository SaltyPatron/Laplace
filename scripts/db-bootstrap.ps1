<#
.SYNOPSIS
  Create the Laplace development database, install the laplace_pg extension,
  and apply schema migrations. Idempotent: safe to re-run.

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

$repoRoot      = Resolve-Path (Join-Path $PSScriptRoot '..')
$migrationsDir = Join-Path $repoRoot 'sql\migrations'

function Invoke-Psql {
  param([string]$Conn, [string]$Sql)
  & psql --quiet --no-psqlrc --no-align --tuples-only --command $Sql $Conn
}

Write-Host "Laplace DB bootstrap" -ForegroundColor Cyan
Write-Host "  Database: $DatabaseName"

# Step 1: ensure database exists
Write-Host "[1/3] Ensure database exists..."
$exists = & psql --quiet --no-psqlrc --no-align --tuples-only `
  --command "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'" `
  $ConnectionString
if (-not $exists) {
  Invoke-Psql $ConnectionString "CREATE DATABASE $DatabaseName"
  Write-Host "  Created database $DatabaseName" -ForegroundColor Green
} else {
  Write-Host "  Database exists" -ForegroundColor Green
}

# Step 2: ensure extension
Write-Host "[2/3] Install laplace_pg extension..."
$dbConn = $ConnectionString -replace 'dbname=\w+', "dbname=$DatabaseName"
Invoke-Psql $dbConn "CREATE EXTENSION IF NOT EXISTS laplace_pg"
Write-Host "  Extension installed" -ForegroundColor Green

# Step 3: apply migrations in order
Write-Host "[3/3] Apply migrations..."
if (Test-Path $migrationsDir) {
  $migrations = Get-ChildItem $migrationsDir -Filter '*.sql' | Sort-Object Name
  foreach ($m in $migrations) {
    Write-Host "  Applying $($m.Name)..."
    & psql --quiet --no-psqlrc --file $m.FullName $dbConn
    if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($m.Name)" }
  }
  Write-Host "  Applied $($migrations.Count) migration(s)" -ForegroundColor Green
} else {
  Write-Warning "No migrations directory found yet — Phase 2 deliverable."
}

Write-Host ""
Write-Host "Bootstrap complete." -ForegroundColor Cyan
