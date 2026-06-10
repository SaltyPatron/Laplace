@echo off
setlocal
rem Verify local extension deploy at D:\Data\Postgres\laplace (no rebuild).
rem Agent rules: .github\instructions\build-environment.instructions.md
call "%~dp0env.cmd"
set "DEPLOY=D:\Data\Postgres\laplace"
set "PGPASSWORD=postgres"
set "PSQL=C:\Program Files\PostgreSQL\18\bin\psql.exe"

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

if "%OK%"=="0" (
  echo.
  echo FIX: scripts\win\build-extensions.cmd ^&^& scripts\win\install-extensions.cmd
  echo See: .github\instructions\build-environment.instructions.md
  exit /b 1
)

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

echo.
echo deploy OK
exit /b 0
