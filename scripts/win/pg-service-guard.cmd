@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  pg-service-guard.cmd — preflight for Postgres service vs orphan postmaster
rem
rem  Exit codes:
rem    0  service RUNNING and (if checked) accepting connections — safe
rem    2  ORPHAN: service not RUNNING but port 5432 is LISTENING
rem    3  DOWN: service not RUNNING and nothing on 5432
rem    4  service RUNNING but not accepting connections yet
rem
rem  No elevation. No start/stop. No database mutations.
rem  Usage:  call scripts\win\pg-service-guard.cmd
rem          call scripts\win\pg-service-guard.cmd --require-up
rem ============================================================================
call "%~dp0env.cmd"
set "SVC=postgresql-x64-18"
set "REQUIRE_UP=0"
if /i "%~1"=="--require-up" set "REQUIRE_UP=1"

set "PORT_OWNER="
for /f "tokens=5" %%P in ('netstat -ano ^| findstr /r /c:":5432 .*LISTENING"') do set "PORT_OWNER=%%P"

sc query %SVC% | findstr /i "RUNNING" >nul
if errorlevel 1 goto not_running

if not defined PORT_OWNER (
  echo pg-service-guard: service RUNNING but port 5432 not listening yet
  if "%REQUIRE_UP%"=="1" exit /b 4
  exit /b 4
)

"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -tAc "SELECT 1;" >nul 2>&1
if errorlevel 1 (
  echo pg-service-guard: service RUNNING, port owned by PID !PORT_OWNER!, but not accepting connections
  exit /b 4
)

echo pg-service-guard: OK — %SVC% RUNNING, port 5432 PID=!PORT_OWNER!, accepting connections
exit /b 0

:not_running
if defined PORT_OWNER (
  echo pg-service-guard: ORPHAN — %SVC% not RUNNING but port 5432 held by PID !PORT_OWNER!
  echo   Do NOT services.msc Start / pg_ctl start / unelevated kill.
  echo   User-elevated reclaim only: cmd /c "scripts\win\reclaim-postgres.cmd"
  exit /b 2
)

echo pg-service-guard: DOWN — %SVC% not RUNNING, port 5432 free
if "%REQUIRE_UP%"=="1" (
  echo   Start the Windows service ^(elevated net start %SVC%^) — never pg_ctl start.
  exit /b 3
)
exit /b 3
