@echo off
setlocal
call "%~dp0env.cmd"
set "DEPLOY=%LAPLACE_DEPLOY%"
set "PSQL=%PGBIN%\psql.exe"
set "T0_SRC=%LAPLACE_PERFCACHE_BIN%"
set "HW_SRC=%LAPLACE_HIGHWAY_PERFCACHE_BIN%"
set "T0_DST=%DEPLOY%\share\laplace_t0_perfcache.bin"
set "HW_DST=%DEPLOY%\share\laplace_highway_perfcache.bin"

echo === Laplace local deploy verification ===
echo deploy root: %DEPLOY%
echo.

set "OK=1"
if not exist "%DEPLOY%\lib\laplace_substrate.dll" (
  echo MISSING: %DEPLOY%\lib\laplace_substrate.dll
  set "OK=0"
) else (
  for %%F in ("%DEPLOY%\lib\laplace_substrate.dll") do echo substrate DLL: %%~zF bytes  %%~tF
)
if not exist "%DEPLOY%\lib\laplace_geom.dll" (
  echo MISSING: %DEPLOY%\lib\laplace_geom.dll
  set "OK=0"
) else (
  for %%F in ("%DEPLOY%\lib\laplace_geom.dll") do echo geom DLL: %%~zF bytes  %%~tF
)
if not exist "%DEPLOY%\share\extension\laplace_substrate.control" (
  echo MISSING: %DEPLOY%\share\extension\laplace_substrate.control
  set "OK=0"
)
if not exist "%DEPLOY%\share\extension\laplace_geom.control" (
  echo MISSING: %DEPLOY%\share\extension\laplace_geom.control
  set "OK=0"
)
if not exist "%T0_DST%" (
  echo MISSING: %T0_DST%
  set "OK=0"
) else (
  for %%F in ("%T0_DST%") do echo T0 perfcache: %%~zF bytes  %%~tF
)
if not exist "%HW_DST%" (
  echo MISSING: %HW_DST%
  set "OK=0"
) else (
  for %%F in ("%HW_DST%") do echo highway perfcache: %%~zF bytes  %%~tF
)

if exist "%T0_SRC%" if exist "%T0_DST%" (
  fc /b "%T0_SRC%" "%T0_DST%" >nul 2>&1 || (
    echo STALE: T0 perfcache deploy != build-win ^(re-run install-extensions.cmd^)
    set "OK=0"
  )
)
if exist "%HW_SRC%" if exist "%HW_DST%" (
  fc /b "%HW_SRC%" "%HW_DST%" >nul 2>&1 || (
    echo STALE: highway perfcache deploy != build-win ^(re-run install-extensions.cmd^)
    set "OK=0"
  )
)

if "%OK%"=="0" (
  echo.
  echo FIX: cmake --build "%LAPLACE_ENGINE_BUILD%" --target laplace_t0_perfcache laplace_highway_perfcache
  echo      scripts\win\build-extensions.cmd ^&^& scripts\win\install-extensions.cmd
  exit /b 1
)

"%PSQL%" -h localhost -U postgres -d postgres -tAc "SELECT 1;" >nul 2>&1
if errorlevel 1 (
  echo.
  echo Postgres: not reachable — file deploy OK, GUC wiring not checked
  echo   start postgresql-x64-18, then: scripts\win\install-extensions.cmd
  goto done_ok
)

echo.
echo postgresql.auto.conf perfcache GUCs:
rem for /f + quoted path with spaces breaks on "C:\Program Files\..." — use psql from PATH
set "PATH=%PGBIN%;%PATH%"
for /f "delims=" %%G in ('psql -h localhost -U postgres -d postgres -tAc "SHOW laplace_substrate.perfcache_path;"') do set "GUC_T0=%%G"
for /f "delims=" %%G in ('psql -h localhost -U postgres -d postgres -tAc "SHOW laplace_substrate.highway_perfcache_path;"') do set "GUC_HW=%%G"
echo   laplace_substrate.perfcache_path = %GUC_T0%
echo   laplace_substrate.highway_perfcache_path = %GUC_HW%
if /i not "%GUC_T0%"=="%LAPLACE_DEPLOY_PG%/share/laplace_t0_perfcache.bin" (
  echo STALE GUC: T0 path does not match deploy tree — run install-extensions.cmd
  set "OK=0"
)
if /i not "%GUC_HW%"=="%LAPLACE_DEPLOY_PG%/share/laplace_highway_perfcache.bin" (
  echo STALE GUC: highway path does not match deploy tree — run install-extensions.cmd
  set "OK=0"
)
if "%OK%"=="0" exit /b 1

echo.
echo pg_available_extensions ^(laplace*^):
"%PSQL%" -h localhost -U postgres -d postgres -tAc "SELECT name || ' ' || default_version FROM pg_available_extensions WHERE name LIKE 'laplace%%' ORDER BY 1;"

echo.
"%PSQL%" -h localhost -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='laplace'" | findstr 1 >nul
if errorlevel 1 (
  echo laplace database: not created yet
) else (
  echo pg_extension in laplace DB:
  "%PSQL%" -h localhost -U postgres -d laplace -tAc "SELECT extname || ' ' || extversion FROM pg_extension WHERE extname LIKE 'laplace%%' ORDER BY 1;"
)

:done_ok
echo.
echo deploy OK
exit /b 0
