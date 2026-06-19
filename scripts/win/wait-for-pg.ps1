# Wait for PostgreSQL on localhost:5432 (up to 2 min, every 5s). Does not start/stop PG.
param([int]$TimeoutSec = 120, [int]$IntervalSec = 5)
$ErrorActionPreference = 'Stop'
$envScript = Join-Path $PSScriptRoot 'env.cmd'
cmd /c "call `"$envScript`" >nul"
$psql = Join-Path $env:PGBIN 'psql.exe'
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
  $tcp = Test-NetConnection -ComputerName localhost -Port 5432 -WarningAction SilentlyContinue
  if ($tcp.TcpTestSucceeded) {
    if (Test-Path $psql) {
      $env:PGPASSWORD = if ($env:PGPASSWORD) { $env:PGPASSWORD } else { 'postgres' }
      $db = if ($env:LAPLACE_DBNAME) { $env:LAPLACE_DBNAME } else { 'laplace' }
      & $psql -h localhost -U postgres -d $db -t -c 'SELECT 1' 2>$null | Out-Null
      if ($LASTEXITCODE -eq 0) { Write-Host "PG ready (psql SELECT 1)"; exit 0 }
    } else {
      Write-Host "PG port open (no psql ping)"
      exit 0
    }
  }
  Write-Host "Waiting for PG on localhost:5432..."
  Start-Sleep -Seconds $IntervalSec
}
Write-Host "PG not ready within ${TimeoutSec}s"
exit 1
