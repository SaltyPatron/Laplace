@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "DEPLOY=%LAPLACE_DEPLOY%"
set "RECYCLE=0"
set "FILES_ONLY=0"
set "SKIP_BUILD=0"
:parse_install
if "%~1"=="" goto parse_install_done
if /i "%~1"=="--recycle" ( set "RECYCLE=1" & shift /1 & goto parse_install )
if /i "%~1"=="--files-only" ( set "FILES_ONLY=1" & shift /1 & goto parse_install )
if /i "%~1"=="--skip-build" ( set "SKIP_BUILD=1" & shift /1 & goto parse_install )
echo install-extensions: unknown flag %~1
exit /b 2
:parse_install_done

set "T0_SRC=%LAPLACE_PERFCACHE_BIN%"
set "HW_SRC=%LAPLACE_HIGHWAY_PERFCACHE_BIN%"
set "T0_DST=%DEPLOY%\share\laplace_t0_perfcache.bin"
set "HW_DST=%DEPLOY%\share\laplace_highway_perfcache.bin"

if not exist "%DEPLOY%\lib" mkdir "%DEPLOY%\lib"
if not exist "%DEPLOY%\share" mkdir "%DEPLOY%\share"
if not exist "%DEPLOY%\share\extension" mkdir "%DEPLOY%\share\extension"
del /q "%DEPLOY%\lib\*.stale~*" 2>nul
del /q "%DEPLOY%\share\*.stale~*" 2>nul

if not exist "%T0_SRC%" (
  echo ERROR: T0 perfcache missing at %T0_SRC%
  echo        run: cmake --build "%LAPLACE_ENGINE_BUILD%" --target laplace_t0_perfcache
  exit /b 1
)
if not exist "%HW_SRC%" (
  echo ERROR: highway perfcache missing at %HW_SRC%
  echo        run: cmake --build "%LAPLACE_ENGINE_BUILD%" --target laplace_highway_perfcache
  exit /b 1
)

if "%SKIP_BUILD%"=="0" (
  cmake --build "%LAPLACE_EXT_BUILD%" --target laplace_geom_sql laplace_substrate_sql || exit /b 1
) else (
  echo install-extensions: SQL target rebuild skipped (--skip-build^)
)

set "GEOM_DIR=%LAPLACE_EXT_BUILD%\laplace_geom"
set "SUB_DIR=%LAPLACE_EXT_BUILD%\laplace_substrate"
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

call :swapcopy "%LAPLACE_EXT_BUILD%\laplace_geom\laplace_geom.dll" || exit /b 1
call :swapcopy "%LAPLACE_EXT_BUILD%\laplace_substrate\laplace_substrate.dll" || exit /b 1
call :swapcopy "%LAPLACE_ENGINE_BUILD%\core\laplace_core.dll" || exit /b 1
call :swapcopy "%LAPLACE_ENGINE_BUILD%\dynamics\laplace_dynamics.dll" || exit /b 1
rem Both perfcache blobs are mmap'd by the postmaster (shared_preload_libraries prewarm).
call :swapcopy "%T0_SRC%" "%DEPLOY%\share" || exit /b 1
call :swapcopy "%HW_SRC%" "%DEPLOY%\share" || exit /b 1
call :swapcopy "C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin\tbb12.dll" || exit /b 1
call :swapcopy "C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin\libhwloc-15.dll"

call :swapcopy "C:\Program Files (x86)\Intel\oneAPI\mkl\latest\bin\mkl_tbb_thread.2.dll" || exit /b 1
call :swapcopy "C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin\libmmd.dll" || exit /b 1
call :swapcopy "C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin\libiomp5md.dll"

call :verify_deploy || exit /b 1
echo deployed: %DEPLOY%

if "%FILES_ONLY%"=="1" (
  echo [install-extensions] files OK — Postgres GUC wiring skipped ^(--files-only^)
  echo   when the service is up, re-run without --files-only to sync postgresql.auto.conf
  exit /b 0
)

set "PSQL=%PGBIN%\psql.exe"
"%PSQL%" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT 1;" >nul 2>&1
if errorlevel 1 (
  echo [install-extensions] ERROR: Postgres is not accepting connections.
  echo   Files are deployed under %DEPLOY% but postgresql.auto.conf was NOT updated.
  echo   Start postgresql-x64-18, then re-run: scripts\win\install-extensions.cmd
  echo   Or cold-bootstrap: install-extensions.cmd --files-only, start PG, install-extensions.cmd again.
  exit /b 1
)

set "GUC_SQL=%TEMP%\laplace-install-gucs.sql"
> "%GUC_SQL%" (
  echo ALTER SYSTEM SET extension_control_path = '%LAPLACE_DEPLOY_PG%/share;$system';
  echo ALTER SYSTEM SET dynamic_library_path = '$libdir;%LAPLACE_DEPLOY_PG%/lib';
  echo ALTER SYSTEM SET laplace_substrate.perfcache_path = '%LAPLACE_DEPLOY_PG%/share/laplace_t0_perfcache.bin';
  echo ALTER SYSTEM SET laplace_substrate.highway_perfcache_path = '%LAPLACE_DEPLOY_PG%/share/laplace_highway_perfcache.bin';
  echo ALTER SYSTEM SET shared_preload_libraries = 'laplace_substrate';
  echo SELECT pg_reload_conf^(^);
)
"%PSQL%" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -f "%GUC_SQL%" || exit /b 1
"%PSQL%" -h localhost -U postgres -d postgres -tAc "SELECT name, default_version FROM pg_available_extensions WHERE name LIKE 'laplace%%' ORDER BY 1;"
if "%RECYCLE%"=="1" (
  echo recycling laplace backends so fresh DLLs/SQL load on next connection...
  "%PSQL%" -h localhost -U postgres -d postgres -tAc "SELECT count(pg_terminate_backend(pid)) || ' backend(s) recycled' FROM pg_stat_activity WHERE datname LIKE 'laplace%%' AND pid <> pg_backend_pid();"
)
echo wired: extension_control_path + dynamic_library_path + both perfcache GUCs -^> %DEPLOY%
exit /b 0

:verify_deploy
set "OK=1"
for %%P in (
  "%DEPLOY%\lib\laplace_substrate.dll"
  "%DEPLOY%\lib\laplace_geom.dll"
  "%DEPLOY%\lib\laplace_core.dll"
  "%T0_DST%"
  "%HW_DST%"
) do (
  if not exist "%%~P" (
    echo ERROR: deploy verify missing %%~P
    set "OK=0"
  )
)
if "%OK%"=="0" exit /b 1
fc /b "%T0_SRC%" "%T0_DST%" >nul 2>&1 || (
  echo ERROR: deployed T0 perfcache does not match build output
  exit /b 1
)
fc /b "%HW_SRC%" "%HW_DST%" >nul 2>&1 || (
  echo ERROR: deployed highway perfcache does not match build output
  exit /b 1
)
for %%F in ("%T0_DST%") do echo   T0 perfcache: %%~zF bytes
for %%F in ("%HW_DST%") do echo   highway perfcache: %%~zF bytes
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
copy /y "%SRC%" "%DESTDIR%\%BASE%" >nul 2>nul && exit /b 0
ren "%DESTDIR%\%BASE%" "%BASE%.stale~%RANDOM%" 2>nul || goto sc_fail
copy /y "%SRC%" "%DESTDIR%\%BASE%" >nul 2>nul || goto sc_fail
echo   hot-swapped %BASE% -- old image renamed; backends pick up the new copy on reconnect
exit /b 0
:sc_fail
echo FAILED to deploy %BASE% into %DESTDIR%
exit /b 1
