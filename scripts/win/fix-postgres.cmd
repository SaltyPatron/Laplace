@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  fix-postgres.cmd — self-service recovery for local Laplace + PostgreSQL 18
rem
rem  RUN FROM ELEVATED CMD (Administrator):
rem    cmd /c "scripts\win\fix-postgres.cmd"
rem
rem  What this fixes:
rem    - Missing extension deploy (D:\Data\Laplace\deploy) — NOT your database.
rem      Table data lives in %LAPLACE_PGDATA%\base\; deleting deploy/ does not erase it.
rem    - Old layout D:\Data\Postgres\laplace\ inside PGDATA (postmaster fsync'd DLLs → crash).
rem    - Empty pg_wal\ (runs pg_resetwal — keeps base\; required after manual WAL delete).
rem    - Stale postgresql.auto.conf GUC paths / preload boot failures.
rem
rem  Flags:
rem    (none)         Full: deploy, wal fix if needed, start PG, wire GUCs, restart, db-reset
rem    --wire-only    Deploy + GUC wire + service restart; NO db-reset (keeps laplace DB as-is)
rem    --continue     PG already running — skip deploy/wal/start; run wire + restart (+ db-reset)
rem    --nuke-db      DROP DATABASE laplace before db-reset (schema wipe, not whole cluster)
rem    --skip-wal     Never run pg_resetwal (use if WAL is intact)
rem
rem  After success: seed-foundation.cmd when you want data back.
rem  Verify anytime:  scripts\win\verify-deploy.cmd  and  scripts\win\status.ps1
rem ============================================================================
call "%~dp0env.cmd"
set "PGDATA=%LAPLACE_PGDATA%"
set "AUTOC=%PGDATA%\postgresql.auto.conf"
set "SVC=postgresql-x64-18"
set "WIRE_ONLY=0"
set "CONTINUE=0"
set "NUKE=0"
set "SKIP_WAL=0"
for %%A in (%*) do (
  if /i "%%~A"=="--wire-only" set "WIRE_ONLY=1"
  if /i "%%~A"=="--continue" set "CONTINUE=1"
  if /i "%%~A"=="--nuke-db" set "NUKE=1"
  if /i "%%~A"=="--skip-wal" set "SKIP_WAL=1"
)

net session >nul 2>&1
if errorlevel 1 (
  echo ERROR: run from Administrator cmd — service start/stop requires elevation.
  echo   Right-click cmd ^> Run as administrator, then re-run this script.
  exit /b 1
)

if not exist "%LAPLACE_PERFCACHE_BIN%" (
  echo ERROR: build T0 perfcache first:
  echo   cmd /c "scripts\win\rebuild-all.cmd --skip-app"
  exit /b 1
)
if not exist "%LAPLACE_HIGHWAY_PERFCACHE_BIN%" (
  echo ERROR: build highway perfcache first ^(same command as above^).
  exit /b 1
)

if "%CONTINUE%"=="0" goto cold_boot

sc query %SVC% | findstr /i "RUNNING" >nul || (
  echo ERROR: --continue requires %SVC% RUNNING — start it or run without --continue
  exit /b 1
)
goto wire_gucs

:cold_boot
echo ==== [1/6] deploy extension tree to %LAPLACE_DEPLOY% ====
call "%~dp0bootstrap-deploy.cmd" || exit /b 1
del /q "%LAPLACE_DEPLOY%\lib\*.stale~*" 2>nul
del /q "%LAPLACE_DEPLOY%\share\*.stale~*" 2>nul

echo ==== [2/6] patch postgresql.auto.conf ^(paths + boot without preload^) ====
powershell -NoProfile -Command ^
  "$p='%AUTOC%'; if (-not (Test-Path $p)) { exit 0 }; $t=Get-Content $p -Raw; " ^
  "$t=$t -replace 'D:/Data/Postgres/laplace','D:/Data/Laplace/deploy'; " ^
  "$t=$t -replace \"shared_preload_libraries = 'laplace_substrate'\",\"shared_preload_libraries = ''\"; " ^
  "Set-Content -Path $p -Value $t -NoNewline"

if "%SKIP_WAL%"=="0" (
  echo ==== [3/6] WAL check ====
  set "WAL_SEG=0"
  for %%F in ("%PGDATA%\pg_wal\0*") do set "WAL_SEG=1"
  if "!WAL_SEG!"=="0" (
    echo pg_wal has no segment files — running pg_resetwal on %PGDATA%
    echo   ^(keeps base\; only needed after WAL was deleted or cluster won't recover^)
    "%PGBIN%\pg_resetwal.exe" -f "%PGDATA%" || exit /b 1
  ) else (
    echo pg_wal segments present — skipping pg_resetwal
  )
) else (
  echo ==== [3/6] WAL check skipped ^(--skip-wal^) ====
)

echo ==== [4/6] start %SVC% ^(no preload yet^) ====
sc query %SVC% | findstr /i "RUNNING" >nul && (
  echo service already running — recycling for clean boot
  net stop %SVC% >nul 2>&1
  ping -n 4 127.0.0.1 >nul
)
net start %SVC% >nul 2>&1 || (
  echo net start failed — check latest %PGDATA%\log\postgresql-*.log
  exit /b 1
)
call :wait_for_pg 30 || (
  echo Postgres did not accept connections — see %PGDATA%\log\
  exit /b 1
)

:wire_gucs
if "%NUKE%"=="1" (
  echo ==== dropping laplace database ^(--nuke-db^) ====
  "%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();"
  "%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS laplace;"
)

echo ==== [5/6] wire GUCs + both perfcache paths ====
call "%~dp0install-extensions.cmd" || exit /b 1

echo ==== [6/6] postmaster restart ^(shared_preload_libraries preload^) ====
net stop %SVC% >nul 2>&1
ping -n 4 127.0.0.1 >nul
net start %SVC% >nul 2>&1 || exit /b 1
call :wait_for_pg 45 || exit /b 1

if "%WIRE_ONLY%"=="1" (
  call "%~dp0verify-deploy.cmd" || exit /b 1
  echo.
  echo FIX OK ^(--wire-only^): deploy + GUCs + preload. laplace database untouched.
  exit /b 0
)

call "%~dp0db-reset.cmd" || exit /b 1
call "%~dp0verify-deploy.cmd" || exit /b 1
echo.
echo FIX OK: Postgres up, laplace DB recreated empty. Run seed-foundation.cmd when ready.
exit /b 0

:wait_for_pg
set "TRIES=%~1"
:wait_loop
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -tAc "SELECT 1;" >nul 2>&1 && exit /b 0
set /a TRIES-=1
if !TRIES! LEQ 0 exit /b 1
ping -n 2 127.0.0.1 >nul
goto wait_loop
