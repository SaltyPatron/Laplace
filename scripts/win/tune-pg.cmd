@echo off
setlocal
call "%~dp0env.cmd"
set "PSQL="%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1"

rem ---------------------------------------------------------------------------
rem tune-pg is PLUMBING ONLY. Every cluster-GUC value is derived from the
rem Cpu/MemoryTopology authorities by the CLI emitter (cpu-topology --pg-tuning),
rem which skips native init and prints ALTER SYSTEM statements. Do NOT hardcode a
rem GB literal or worker count here — change MemoryTopology.cs / CpuTopology.cs and
rem the machine re-denotes them. This is the single home for cluster tuning.
rem ---------------------------------------------------------------------------
set "PGTUNE_SQL=%TEMP%\laplace-pg-tuning.sql"

pushd "%LAPLACE_ROOT%\app" >nul 2>&1
if not exist "%LAPLACE_CLI_EXE%" (
  dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q --nologo >nul 2>&1
)
"%LAPLACE_CLI_EXE%" cpu-topology --pg-tuning > "%PGTUNE_SQL%" 2>nul
popd >nul 2>&1

if not exist "%PGTUNE_SQL%" ( echo tune-pg: FAILED to emit tuning SQL & exit /b 1 )
echo tune-pg: applying machine-derived cluster GUCs ^(Cpu/MemoryTopology^):
type "%PGTUNE_SQL%"
%PSQL% -f "%PGTUNE_SQL%" || exit /b 1

rem Preflight: refuse orphan / down states. Never UAC. Never pg_ctl start.
call "%~dp0pg-service-guard.cmd"
if errorlevel 2 if not errorlevel 3 (
  echo tune-pg: GUCs applied; restart BLOCKED — orphan postmaster. User must run reclaim-postgres.cmd elevated.
  exit /b 4
)
if errorlevel 3 (
  echo tune-pg: GUCs applied; restart BLOCKED — service down. Elevated: net start postgresql-x64-18
  exit /b 3
)

powershell -NoProfile -Command "Restart-Service postgresql-x64-18 -ErrorAction Stop"
if errorlevel 1 (
  rem GUCs + pg_reload_conf already applied; service is up. Soft-ok so unelevated
  rem pipelines (Tony_Hart-Desktop) do not abort — pending_restart waits for a later elevated bounce.
  echo tune-pg: Restart-Service denied ^(not elevated^). GUCs are applied; pending_restart may remain.
  echo   When YOU elevate ^(no agent UAC^): net stop postgresql-x64-18 ^&^& net start postgresql-x64-18
  echo   Or: cmd /c "scripts\win\reclaim-postgres.cmd"
)

:verify
%PSQL% -P pager=off -c "SELECT name, setting, unit, pending_restart FROM pg_settings WHERE name IN ('shared_buffers','effective_cache_size','synchronous_commit','max_wal_size','work_mem','maintenance_work_mem','max_connections','max_parallel_workers','max_parallel_maintenance_workers','wal_buffers','random_page_cost','effective_io_concurrency') ORDER BY name;"
if errorlevel 1 (
  echo tune-pg: verify query failed — Postgres not accepting connections
  exit /b 1
)
exit /b 0
