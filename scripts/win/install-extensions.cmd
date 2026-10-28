@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd" || exit /b 1
cd /d "%LAPLACE_ROOT%"
set "DEPLOY=%LAPLACE_DEPLOY%"
set "RECYCLE=0"
set "FORCE_RECYCLE=0"
set "RUNTIME_CHANGED=0"
set "FILES_ONLY=0"
set "SKIP_BUILD=0"
set "CHECK_CONNECTION=0"
:parse_install
if "%~1"=="" goto parse_install_done
if /i "%~1"=="--recycle" ( set "RECYCLE=1" & shift /1 & goto parse_install )
if /i "%~1"=="--force-recycle" ( set "RECYCLE=1" & set "FORCE_RECYCLE=1" & shift /1 & goto parse_install )
if /i "%~1"=="--files-only" ( set "FILES_ONLY=1" & shift /1 & goto parse_install )
if /i "%~1"=="--skip-build" ( set "SKIP_BUILD=1" & shift /1 & goto parse_install )
if /i "%~1"=="--check-connection" ( set "CHECK_CONNECTION=1" & shift /1 & goto parse_install )
echo install-extensions: unknown flag %~1
exit /b 2
:parse_install_done
if "%CHECK_CONNECTION%"=="1" if "%FILES_ONLY%"=="1" (
  echo install-extensions: --check-connection and --files-only cannot be combined
  exit /b 2
)
rem Authenticate before building or replacing any installed files. Keep the
rem actual libpq error in the deployment stage log; connection failure is not
rem evidence that the Windows service is stopped.
set "PSQL=%PGBIN%\psql.exe"
if not defined PGCONNECT_TIMEOUT set "PGCONNECT_TIMEOUT=10"
set "PGAPPNAME=laplace-install-extensions"
if "%FILES_ONLY%"=="0" (
  call :check_connection
  if errorlevel 1 exit /b 1
)
if "%CHECK_CONNECTION%"=="1" exit /b 0

set "T0_SRC=%LAPLACE_PERFCACHE_BIN%"
for %%F in ("%T0_SRC%") do set "FLAGS_SRC=%%~dpnF.flags"
set "HW_SRC=%LAPLACE_HIGHWAY_PERFCACHE_BIN%"
set "CHESS_SRC=%LAPLACE_CHESS_POSITION_PERFCACHE_BIN%"
set "TRANSITION_SRC=%LAPLACE_ENGINE_BUILD%\core\perfcache\laplace_chess_transition_perfcache.bin"
set "T0_DST=%DEPLOY%\share\tier0.bin"
set "HW_DST=%DEPLOY%\share\highway.bin"
set "CHESS_DST=%DEPLOY%\share\laplace_chess_position_perfcache.bin"

if not exist "%DEPLOY%\lib" mkdir "%DEPLOY%\lib"
if not exist "%DEPLOY%\share" mkdir "%DEPLOY%\share"
if not exist "%DEPLOY%\share\extension" mkdir "%DEPLOY%\share\extension"
del /q "%DEPLOY%\lib\*.stale~*" 2>nul
del /q "%DEPLOY%\share\*.stale~*" 2>nul
rem Locked stale leftovers: move out of deploy\lib on the same volume (MoveFile across drives copies+deletes, failing on mapped DLLs).
powershell -NoProfile -Command ^
  "$d='%DEPLOY%\lib'; $t='%LAPLACE_WORK%\deploy-stale'; New-Item -ItemType Directory -Force -Path $t | Out-Null;" ^
  "Get-ChildItem -LiteralPath $t -Filter '*.stale~*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue;" ^
  "Get-ChildItem -LiteralPath $d -Filter '*.stale~*' -ErrorAction SilentlyContinue | ForEach-Object {" ^
  "  Move-Item -LiteralPath $_.FullName -Destination (Join-Path $t $_.Name) -Force -ErrorAction SilentlyContinue }"

if not exist "%T0_SRC%" (
  echo ERROR: T0 perfcache missing at %T0_SRC%
  echo        run: cmake --build "%LAPLACE_ENGINE_BUILD%" --target laplace_t0_perfcache
  exit /b 1
)
for %%F in ("%FLAGS_SRC%" "%FLAGS_SRC%.layout") do if not exist "%%~F" (
  echo ERROR: Tier-0 flags missing at %%~F
  exit /b 1
)
if not exist "%HW_SRC%" (
  echo ERROR: highway perfcache missing at %HW_SRC%
  echo        run: cmake --build "%LAPLACE_ENGINE_BUILD%" --target laplace_highway_perfcache
  exit /b 1
)
if not exist "%CHESS_SRC%" (
  echo ERROR: chess position perfcache missing at %CHESS_SRC%
  echo        run: cmake --build "%LAPLACE_ENGINE_BUILD%" --target laplace_chess_position_perfcache
  exit /b 1
)
if not exist "%TRANSITION_SRC%" (
  echo ERROR: chess transition perfcache missing at %TRANSITION_SRC%
  echo        run: cmake --build "%LAPLACE_ENGINE_BUILD%" --target laplace_chess_transition_perfcache
  exit /b 1
)

if "%SKIP_BUILD%"=="0" (
  call "%~dp0build-extensions.cmd" laplace_geom_sql laplace_substrate_sql || exit /b 1
) else (
  echo install-extensions: SQL target rebuild skipped (--skip-build^)
)

set "GEOM_DIR=%LAPLACE_EXT_BUILD%\laplace_geom"
set "SUB_DIR=%LAPLACE_EXT_BUILD%\laplace_substrate"
set "PG_SOURCE=%LAPLACE_SRC%\Laplace-postgres"
for %%F in ("%PG_SOURCE%\laplace.control" "%LAPLACE_PG_EXTENSION_BUILD%\laplace.dll") do if not exist "%%~F" (
  echo ERROR: missing %%~F -- run build-extensions.cmd first
  exit /b 1
)
for /f "delims=" %%P in ('"%PGBIN%\pg_config.exe" --pkglibdir') do set "PG_MODULE_DIR=%%P"
if not defined PG_MODULE_DIR exit /b 1
if not exist "%GEOM_DIR%\laplace_geom.control" (
  echo ERROR: missing %GEOM_DIR%\laplace_geom.control — run build-extensions.cmd first
  exit /b 1
)
if not exist "%SUB_DIR%\laplace_substrate.control" (
  echo ERROR: missing %SUB_DIR%\laplace_substrate.control — run build-extensions.cmd first
  exit /b 1
)

copy /y "%GEOM_DIR%\laplace_geom.control" "%DEPLOY%\share\extension\" >nul || exit /b 1
for %%F in ("%GEOM_DIR%\laplace_geom--*.sql") do copy /y "%%F" "%DEPLOY%\share\extension\" >nul || exit /b 1

copy /y "%SUB_DIR%\laplace_substrate.control" "%DEPLOY%\share\extension\" >nul || exit /b 1
for %%F in ("%SUB_DIR%\laplace_substrate--*.sql") do copy /y "%%F" "%DEPLOY%\share\extension\" >nul || exit /b 1
copy /y "%SUB_DIR%\laplace_substrate_upgrade.sql" "%DEPLOY%\share\extension\" >nul || exit /b 1

rem Existing functions bind $libdir/laplace; dynamic_library_path alone cannot
rem redirect that absolute module reference. Publish the module there and its
rem matching control/upgrade scripts in the declared extension search path.
call :swapcopy "%LAPLACE_PG_EXTENSION_BUILD%\laplace.dll" "%PG_MODULE_DIR%" || exit /b 1
copy /y "%PG_SOURCE%\laplace.control" "%DEPLOY%\share\extension\" >nul || exit /b 1
if not exist "%LAPLACE_PG_EXTENSION_BUILD%\extension\laplace--*.sql" (
  echo ERROR: missing generated Native extension SQL -- run build-extensions.cmd first
  exit /b 1
)
for %%F in ("%LAPLACE_PG_EXTENSION_BUILD%\extension\laplace--*.sql") do copy /y "%%F" "%DEPLOY%\share\extension\" >nul || exit /b 1

call :swapcopy "%LAPLACE_EXT_BUILD%\laplace_geom\laplace_geom.dll" || exit /b 1
call :swapcopy "%LAPLACE_EXT_BUILD%\laplace_substrate\laplace_substrate.dll" || exit /b 1
if not exist "%SUB_DIR%\laplace_execution_module.txt" exit /b 1
set /p EXECUTION_MODULE=<"%SUB_DIR%\laplace_execution_module.txt"
call :swapcopy "%SUB_DIR%\!EXECUTION_MODULE!.dll" || exit /b 1
copy /y "%SUB_DIR%\laplace_execution_module.txt" "%DEPLOY%\share\extension\" >nul || exit /b 1
rem A backend module's dependencies are not searched beside the module: PostgreSQL loads it with LoadLibrary
rem (src/port/win32dlopen.c), so Windows looks beside postgres.exe and on the service's PATH. Every DLL a module
rem needs at run time therefore goes beside postgres.exe, as PostGIS's install puts geos and proj there.
set "PGRT=%LAPLACE_PG_PREFIX%\bin"
call :swapcopy "%LAPLACE_ENGINE_BUILD%\core\laplace_core.dll" "%PGRT%" || exit /b 1
call :swapcopy "%LAPLACE_ENGINE_BUILD%\dynamics\laplace_dynamics.dll" "%PGRT%" || exit /b 1
rem Perfcache blobs are mmap'd by the postmaster (shared_preload_libraries prewarm).
call :swapcopy "%T0_SRC%" "%DEPLOY%\share" || exit /b 1
call :swapcopy "%FLAGS_SRC%" "%DEPLOY%\share" || exit /b 1
call :swapcopy "%FLAGS_SRC%.layout" "%DEPLOY%\share" || exit /b 1
call :swapcopy "%HW_SRC%" "%DEPLOY%\share" || exit /b 1
call :swapcopy "%CHESS_SRC%" "%DEPLOY%\share" || exit /b 1
call :swapcopy "%TRANSITION_SRC%" "%DEPLOY%\share" || exit /b 1
rem laplace_core's own run-time dependencies, the one list (engine-runtime.cmd): beside postgres.exe, where a backend
rem module's dependencies are searched (LOAD_WITH_ALTERED_SEARCH_PATH), not on the service's PATH.
call "%~dp0engine-runtime.cmd" "%PGRT%" || exit /b 1
if "%LAPLACE_RUNTIME_CHANGED%"=="1" set "RUNTIME_CHANGED=1"

rem geos/proj/sqlite are STATIC into laplace_geom — no runtime DLLs to deploy for them.

call :verify_deploy || exit /b 1
echo deployed: %DEPLOY%

if "%FILES_ONLY%"=="1" (
  echo [install-extensions] files OK — Postgres GUC wiring skipped ^(--files-only^)
  echo   when the service is up, re-run without --files-only to sync postgresql.auto.conf
  exit /b 0
)

set "GUC_SQL=%TEMP%\laplace-install-gucs.sql"
> "%GUC_SQL%" (
  echo ALTER SYSTEM SET extension_control_path = '%LAPLACE_DEPLOY_PG%/share;$system';
  echo ALTER SYSTEM SET dynamic_library_path = '%LAPLACE_DEPLOY_PG%/lib;$libdir';
  echo ALTER SYSTEM SET laplace_substrate.perfcache_path = '%LAPLACE_DEPLOY_PG%/share/tier0.bin';
  echo ALTER SYSTEM SET laplace_substrate.highway_perfcache_path = '%LAPLACE_DEPLOY_PG%/share/highway.bin';
  echo ALTER SYSTEM SET laplace.tier0 = '%LAPLACE_DEPLOY_PG%/share/tier0.bin';
  echo ALTER SYSTEM SET laplace.flags = '%LAPLACE_DEPLOY_PG%/share/tier0.flags';
  echo ALTER SYSTEM SET laplace.highway = '%LAPLACE_DEPLOY_PG%/share/highway.bin';
  echo ALTER SYSTEM SET laplace_substrate.chess_position_perfcache_path = '%LAPLACE_DEPLOY_PG%/share/laplace_chess_position_perfcache.bin';
  rem laplace_substrate joins whatever the server already preloads (observability modules on a shared server), never replaces it
  echo SELECT CASE WHEN current_setting^('shared_preload_libraries'^) ~ '\mlaplace_substrate\M' THEN current_setting^('shared_preload_libraries'^) WHEN current_setting^('shared_preload_libraries'^) = '' THEN 'laplace_substrate' ELSE current_setting^('shared_preload_libraries'^) ^|^| ',laplace_substrate' END AS spl \gset
  rem unquoted: a list setting takes its elements one by one; one quoted text holding commas would be one library name
  echo ALTER SYSTEM SET shared_preload_libraries = :spl;
  echo DO $$ BEGIN IF NOT pg_reload_conf^(^) THEN RAISE EXCEPTION 'PostgreSQL rejected the configuration reload request' USING ERRCODE = '55000'; END IF; END $$;
)
"%PSQL%" -X -w -h "%LAPLACE_PGHOST%" -p "%LAPLACE_PGPORT%" -U "%LAPLACE_PGUSER%" -d postgres -v ON_ERROR_STOP=1 -f "%GUC_SQL%" || exit /b 1
"%PSQL%" -X -w -h "%LAPLACE_PGHOST%" -p "%LAPLACE_PGPORT%" -U "%LAPLACE_PGUSER%" -d postgres -v ON_ERROR_STOP=1 -tAc "SELECT name, default_version FROM pg_available_extensions WHERE name LIKE 'laplace%%' ORDER BY 1;" || exit /b 1
call :recycle_backends || exit /b 1
goto clients_refreshed

:recycle_backends
rem DLL publication does not grant ownership of existing database sessions.
rem Idle Native connections can belong to an ingest between working windows;
rem filtering only pg_stat_activity.state would still terminate those jobs.
rem IIS activation reconnects its own pools. Other clients finish on the runtime
rem they loaded. Only explicitly requested maintenance may force them.
if not "%FORCE_RECYCLE%"=="1" (
  echo Existing PostgreSQL sessions retained; application pools reconnect during activation.
  if "%RECYCLE%"=="1" echo --recycle no longer terminates external clients; explicit maintenance requires --force-recycle.
  exit /b 0
)
if "%FORCE_RECYCLE%"=="1" (
  echo recycling laplace backends so fresh DLLs/SQL load on next connection...
  > "%GUC_SQL%" echo DO $$ DECLARE recycled_count integer; recycled_ok boolean; BEGIN WITH attempts AS MATERIALIZED ^(SELECT pg_terminate_backend^(pid^) AS recycled FROM pg_stat_activity WHERE datname LIKE 'laplace%%' AND pid ^<^> pg_backend_pid^(^)^) SELECT count^(*^) FILTER ^(WHERE recycled^), COALESCE^(bool_and^(recycled^),true^) INTO recycled_count,recycled_ok FROM attempts; IF NOT recycled_ok THEN RAISE EXCEPTION 'PostgreSQL could not recycle every selected backend' USING ERRCODE = '55000'; END IF; RAISE NOTICE '%% backend^(s^) recycled',recycled_count; END $$;
  "%PSQL%" -X -w -h "%LAPLACE_PGHOST%" -p "%LAPLACE_PGPORT%" -U "%LAPLACE_PGUSER%" -d postgres -v ON_ERROR_STOP=1 -f "%GUC_SQL%" || exit /b 1
)
exit /b 0

:clients_refreshed
echo wired: extension_control_path + dynamic_library_path + t0/highway/chess perfcache GUCs -^> %DEPLOY%
exit /b 0

:check_connection
echo install-extensions: authenticate %LAPLACE_PGHOST%:%LAPLACE_PGPORT%/postgres as %LAPLACE_PGUSER%
"%PSQL%" -X -w -h "%LAPLACE_PGHOST%" -p "%LAPLACE_PGPORT%" -U "%LAPLACE_PGUSER%" -d postgres -v ON_ERROR_STOP=1 -Atc "SELECT current_user, system_user, inet_server_addr(), current_setting('data_directory');"
if not errorlevel 1 exit /b 0
echo [install-extensions] ERROR: PostgreSQL authentication/connection check failed; see the client error above.
echo   No extension files or server settings were changed by this invocation.
echo   Windows process identity:
whoami
echo   Verify the declared host, port, database role and TLS/authentication settings.
echo   For an intentionally stopped server, --files-only stages files without configuring PostgreSQL.
exit /b 1

:verify_deploy
set "OK=1"
for %%P in (
  "%PG_MODULE_DIR%\laplace.dll"
  "%DEPLOY%\share\extension\laplace.control"
  "%DEPLOY%\lib\laplace_substrate.dll"
  "%DEPLOY%\lib\laplace_geom.dll"
  "%PGRT%\laplace_core.dll"
  "%PGRT%\svml_dispmd.dll"
  "%T0_DST%"
  "%HW_DST%"
  "%CHESS_DST%"
) do (
  if not exist "%%~P" (
    echo ERROR: deploy verify missing %%~P
    set "OK=0"
  )
)
if "%OK%"=="0" exit /b 1
fc /b "%LAPLACE_PG_EXTENSION_BUILD%\laplace.dll" "%PG_MODULE_DIR%\laplace.dll" >nul 2>&1 || (
  echo ERROR: deployed Native PostgreSQL module does not match build output
  exit /b 1
)
fc /b "%T0_SRC%" "%T0_DST%" >nul 2>&1 || (
  echo ERROR: deployed T0 perfcache does not match build output
  exit /b 1
)
for %%F in ("%FLAGS_SRC%" "%FLAGS_SRC%.layout") do fc /b "%%~F" "%DEPLOY%\share\%%~nxF" >nul 2>&1 || (
  echo ERROR: deployed Tier-0 flags do not match build output
  exit /b 1
)
fc /b "%HW_SRC%" "%HW_DST%" >nul 2>&1 || (
  echo ERROR: deployed highway perfcache does not match build output
  exit /b 1
)
fc /b "%CHESS_SRC%" "%CHESS_DST%" >nul 2>&1 || (
  echo ERROR: deployed chess position perfcache does not match build output
  exit /b 1
)
for %%F in ("%T0_DST%") do echo   T0 perfcache: %%~zF bytes
for %%F in ("%HW_DST%") do echo   highway perfcache: %%~zF bytes
for %%F in ("%CHESS_DST%") do echo   chess position perfcache: %%~zF bytes
exit /b 0

:swapcopy
set "SRC=%~1"
set "BASE=%~nx1"
set "DESTDIR=%~2"
if "%DESTDIR%"=="" set "DESTDIR=%DEPLOY%\lib"
if exist "%SRC%" goto sc_copy
echo missing build artifact: "%SRC%"
exit /b 1
:sc_copy
if exist "%DESTDIR%\%BASE%" (
  fc /b "%SRC%" "%DESTDIR%\%BASE%" >nul 2>nul
  if not errorlevel 1 exit /b 0
)
set "RUNTIME_CHANGED=1"
copy /y "%SRC%" "%DESTDIR%\%BASE%" >nul 2>nul && exit /b 0
rem Locked DLL: rename aside, then MOVE the stale image OUT of deploy (not next to
rem live libs). Leaving *.stale~* under deploy\lib has crashed the postmaster
rem (VCRUNTIME / fsync of hot-swap leftovers).
set "STALE_NAME=%BASE%.stale~%RANDOM%"
set "STALE_DIR=%DESTDIR%\..\.stale"
if not exist "%STALE_DIR%" mkdir "%STALE_DIR%" >nul 2>&1
del /q "%STALE_DIR%\*.stale~*" >nul 2>&1
ren "%DESTDIR%\%BASE%" "%STALE_NAME%" 2>nul || goto sc_fail
copy /y "%SRC%" "%DESTDIR%\%BASE%" >nul 2>nul || goto sc_fail
move /y "%DESTDIR%\%STALE_NAME%" "%STALE_DIR%\%STALE_NAME%" >nul 2>nul
if exist "%DESTDIR%\%STALE_NAME%" (
  echo ERROR: could not move %STALE_NAME% out of deploy — delete it before continuing
  exit /b 1
)
echo   hot-swapped %BASE% -- old image moved to %STALE_DIR% ^(reconnect to load new DLL^)
exit /b 0
:sc_fail
echo FAILED to deploy %BASE% into %DESTDIR%
echo   If the file is locked, recycle backends or reclaim the service — do not leave stale~ in deploy.
exit /b 1
