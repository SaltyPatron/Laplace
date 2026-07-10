@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  reclaim-postgres.cmd — fix SCM Stopped + orphan postmaster on 5432
rem
rem  WHY THIS EXISTS:
rem    Crash / timed-out pg_ctl wait leaves postgres.exe alive while
rem    postgresql-x64-18 is STOPPED. Services.msc Start then fails with
rem    "started and then stopped" because port 5432 is already taken.
rem    Unelevated shells cannot signal NetworkService-owned PIDs
rem    (pg_ctl: Operation not permitted).
rem
rem  RUN FROM ELEVATED CMD (Administrator):
rem    cmd /c "scripts\win\reclaim-postgres.cmd"
rem
rem  What it does:
rem    1. Detect service vs live postmaster / port 5432 mismatch
rem    2. Delete deploy hot-swap leftovers (*.stale~*) that poison fsync
rem    3. Graceful pg_ctl stop of the orphan (or taskkill if signal denied)
rem    4. net start postgresql-x64-18 so SCM owns the postmaster again
rem    5. Print next step (db-reset if schema missing)
rem
rem  Does NOT drop databases. Does NOT seed. Does NOT pop UAC — you already elevated.
rem  Agents must NEVER launch this via Start-Process -Verb RunAs.
rem ============================================================================
call "%~dp0env.cmd"
set "PGDATA=%LAPLACE_PGDATA%"
if not defined PGDATA set "PGDATA=D:\Data\Postgres"
set "SVC=postgresql-x64-18"
set "PGCTL=%PGBIN%\pg_ctl.exe"
set "PSQL=%PGBIN%\psql.exe"

net session >nul 2>&1
if errorlevel 1 (
  echo ERROR: reclaim requires Administrator — NetworkService postmasters cannot be signaled otherwise.
  echo   Right-click cmd ^> Run as administrator, then:
  echo   cmd /c "scripts\win\reclaim-postgres.cmd"
  exit /b 1
)

echo ==== [1/5] diagnose ====
sc query %SVC% | findstr /i "STATE"
set "PORT_OWNER="
for /f "tokens=5" %%P in ('netstat -ano ^| findstr /r /c:":5432 .*LISTENING"') do set "PORT_OWNER=%%P"
if defined PORT_OWNER (
  echo port 5432 LISTENING owner PID=!PORT_OWNER!
) else (
  echo port 5432 not listening
)
if exist "%PGDATA%\postmaster.pid" (
  echo postmaster.pid:
  type "%PGDATA%\postmaster.pid"
) else (
  echo postmaster.pid: missing
)

sc query %SVC% | findstr /i "RUNNING" >nul
if not errorlevel 1 (
  if defined PORT_OWNER (
    echo.
    echo OK: service RUNNING and port 5432 owned — nothing to reclaim.
    goto verify_schema
  )
)

echo ==== [2/5] clear deploy hot-swap leftovers ====
del /q "%LAPLACE_DEPLOY%\lib\*.stale~*" 2>nul
del /q "%LAPLACE_DEPLOY%\share\*.stale~*" 2>nul
echo stale~ cleaned under %LAPLACE_DEPLOY%

echo ==== [3/5] stop orphan postmaster ^(graceful^) ====
if not defined PORT_OWNER (
  echo no listener on 5432 — skip stop
  goto start_svc
)

"%PGCTL%" stop -D "%PGDATA%" -m fast -t 90
if errorlevel 1 (
  echo pg_ctl stop failed — forcing PID !PORT_OWNER! ^(orphan not owned by this session^)
  taskkill /PID !PORT_OWNER! /T /F >nul 2>&1
)

rem Wait until 5432 is free (max ~30s)
set /a WAIT=30
:wait_port
netstat -ano | findstr /r /c:":5432 .*LISTENING" >nul
if errorlevel 1 goto port_free
set /a WAIT-=1
if !WAIT! LEQ 0 (
  echo ERROR: port 5432 still held after stop — aborting before net start.
  netstat -ano | findstr ":5432"
  exit /b 1
)
ping -n 2 127.0.0.1 >nul
goto wait_port
:port_free
echo port 5432 free

:start_svc
echo ==== [4/5] start %SVC% under SCM ====
net start %SVC%
if errorlevel 1 (
  echo ERROR: net start failed — see latest %PGDATA%\log\postgresql-*.log
  echo   Common cause: still-busy 5432, bad shared_preload_libraries, or crash loop.
  exit /b 1
)

set /a WAIT=45
:wait_pg
"%PSQL%" -h localhost -U postgres -d postgres -tAc "SELECT 1;" >nul 2>&1 && goto pg_up
set /a WAIT-=1
if !WAIT! LEQ 0 (
  echo ERROR: service reported started but Postgres is not accepting connections.
  exit /b 1
)
ping -n 2 127.0.0.1 >nul
goto wait_pg
:pg_up
echo Postgres accepting connections under %SVC%

:verify_schema
echo ==== [5/5] schema check ====
"%PSQL%" -h localhost -U postgres -d laplace -tAc "SELECT extname FROM pg_extension WHERE extname='laplace_substrate';" 2>nul | findstr /i "laplace_substrate" >nul
if errorlevel 1 (
  echo laplace_substrate MISSING — schema not installed in DB laplace.
  echo   Non-destructive restore ^(only if the user asked^):
  echo     psql -h localhost -U postgres -d laplace -c "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS laplace_geom; CREATE EXTENSION IF NOT EXISTS laplace_substrate;"
  echo   Do NOT run db-reset unless the user explicitly requested a database reset.
  exit /b 0
)
echo laplace_substrate present.
"%PSQL%" -h localhost -U postgres -d laplace -tAc "SET search_path=laplace,public; SELECT senses(word_id('dog')) AS dog_senses;" 2>nul
echo.
echo RECLAIM OK: service owns Postgres. Do not Start from services.msc while an orphan holds 5432.
exit /b 0
