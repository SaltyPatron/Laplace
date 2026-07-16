@echo off
rem gate-remote — preflight for seeding a REMOTE PG (LAPLACE_PGHOST != localhost).
rem 1) remote reachable as %LAPLACE_PGUSER%   2) laplace_substrate available
rem 3) PENDING-gate: wait while a live CI run holds the hart-server runner
rem    (CI deploy bounces the remote postmaster mid-seed otherwise)
rem 4) warn if cluster has settings pending a postmaster restart (perfcache prewarm inactive)
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

echo ==== gate-remote: host=%LAPLACE_PGHOST% user=%LAPLACE_PGUSER% db=%LAPLACE_DBNAME% ====

"%PGBIN%\psql.exe" -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d postgres -tAc "SELECT 'remote-ok: '||current_setting('server_version')" || (
  echo ERROR: cannot reach %LAPLACE_PGHOST% as %LAPLACE_PGUSER% — check service/pg_hba/role
  exit /b 1
)

set "EXT_AVAIL="
for /f "usebackq delims=" %%v in (`"%PGBIN%\psql.exe" -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d postgres -tAc "SELECT default_version FROM pg_available_extensions WHERE name='laplace_substrate'"`) do set "EXT_AVAIL=%%v"
if not defined EXT_AVAIL (
  echo ERROR: laplace_substrate not in pg_available_extensions on %LAPLACE_PGHOST% — CI install owed
  exit /b 1
)
echo gate-remote: laplace_substrate available=%EXT_AVAIL%

:ci_gate
set "CI_LIVE="
for /f "usebackq delims=" %%n in (`gh run list -R SaltyPatron/Laplace --status in_progress --limit 1 --json databaseId --jq length 2^>nul`) do set "CI_LIVE=%%n"
if not defined CI_LIVE (
  echo gate-remote: WARNING gh unavailable — cannot verify runner is idle; proceeding
  goto ci_done
)
if not "%CI_LIVE%"=="0" (
  echo gate-remote: PENDING — live CI run on the hart-server runner; waiting 60s
  ping -n 61 127.0.0.1 >nul
  goto ci_gate
)
set "CI_QUEUED="
for /f "usebackq delims=" %%n in (`gh run list -R SaltyPatron/Laplace --status queued --limit 1 --json databaseId --jq length 2^>nul`) do set "CI_QUEUED=%%n"
if "%CI_QUEUED%"=="1" (
  echo gate-remote: PENDING — queued CI run; waiting 60s
  ping -n 61 127.0.0.1 >nul
  goto ci_gate
)
:ci_done

set "PENDING="
for /f "usebackq delims=" %%n in (`"%PGBIN%\psql.exe" -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d postgres -tAc "SELECT count(*) FROM pg_settings WHERE pending_restart"`) do set "PENDING=%%n"
if not "%PENDING%"=="0" (
  echo gate-remote: WARNING %PENDING% setting^(s^) pending postmaster restart on %LAPLACE_PGHOST%
  echo   seeds still run, but perfcache preload is inactive until the next restart ^(CI deploy restarts it^)
)

echo gate-remote: OK
exit /b 0
