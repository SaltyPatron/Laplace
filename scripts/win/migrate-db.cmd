@echo off
rem migrate-db — create + apply the declared schema (db/schema) to %LAPLACE_DBNAME% on %LAPLACE_PGHOST% via Laplace.Migrations
rem (up creates the DB; the declaration installs laplace_substrate from the
rem HOST-side available extension). Then gate on schema generation: post-PR#322 substrate
rem is two-axis partitioned — laplace.entities must be relkind 'p'. A plain 'r' means the
rem host's installed extension predates the greenfield schema and seeding would target the
rem wrong generation: stop and deploy first.
setlocal EnableDelayedExpansion
call "%~dp0env.cmd" || exit /b 1
set "MIG=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Migrations\Release\net10.0\Laplace.Migrations.dll"
if not exist "%MIG%" (
  echo migrate-db: building Laplace.Migrations ^(dll missing^)
  dotnet build "%LAPLACE_ROOT%\app\Laplace.Migrations\Laplace.Migrations.csproj" -c Release -v q --nologo || exit /b 1
)

echo ==== migrate-db: %LAPLACE_PGHOST%/%LAPLACE_DBNAME% ====
dotnet "%MIG%" up || exit /b 1

set "RELKIND="
for /f "usebackq delims=" %%k in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='laplace' AND c.relname='entities'"`) do set "RELKIND=%%k"
if not "%RELKIND%"=="p" (
  echo ERROR: laplace.entities relkind='%RELKIND%' — expected 'p' ^(two-axis partitioned, PR#322^)
  echo   the extension installed on %LAPLACE_PGHOST% predates the greenfield schema.
  echo   Fix: push main, then run CI to rebuild+deploy the extension on the host:
  echo     gh workflow run laplace.yml --repo SaltyPatron/Laplace --ref main
  echo   then drop the stale DB there and rerun this script.
  exit /b 1
)

rem Native owns the canonical public schema and has a normal versioned upgrade chain.
psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -v ON_ERROR_STOP=1 -c "SET lock_timeout='20s'; ALTER EXTENSION laplace UPDATE;" || exit /b 1

rem The installed extension brought to the version the host has (pipeline.sh does the same on
rem Linux): a migration only creates a missing extension, so without this a rebuilt
rem extension never reaches a database that already has one. The version is a content hash
rem with no chain of upgrade scripts; the generic upgrade script is the bridge between the
rem installed version and the available one. laplace_geom goes first: laplace_substrate builds on it.
call :bridge laplace_geom || exit /b 1
call :bridge laplace_substrate || exit /b 1
rem laplace_geom retires a function only once laplace_substrate (upgraded just above) no longer uses it.
psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -v ON_ERROR_STOP=1 -c "DO $retire$ BEGIN IF to_regproc('public.laplace_geom_retire') IS NOT NULL THEN PERFORM public.laplace_geom_retire(); END IF; END $retire$;" || exit /b 1
echo migrate-db: OK ^(schema generation verified: partitioned^)
exit /b 0
:bridge
set "EXT_INSTALLED=" & set "EXT_AVAILABLE="
for /f "usebackq delims=" %%v in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT extversion FROM pg_extension WHERE extname='%1'"`) do set "EXT_INSTALLED=%%v"
for /f "usebackq delims=" %%v in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT default_version FROM pg_available_extensions WHERE name='%1'"`) do set "EXT_AVAILABLE=%%v"
if not defined EXT_AVAILABLE ( echo ERROR: %1 is not available on %LAPLACE_PGHOST% & exit /b 1 )
if not defined EXT_INSTALLED exit /b 0
if "%EXT_INSTALLED%"=="%EXT_AVAILABLE%" goto :bridge_done
echo migrate-db: %1 %EXT_INSTALLED% -^> %EXT_AVAILABLE%
if not exist "%LAPLACE_DEPLOY%\share\extension\%1_upgrade.sql" ( echo ERROR: %1_upgrade.sql is not installed; run install-extensions.cmd & exit /b 1 )
copy /y "%LAPLACE_DEPLOY%\share\extension\%1_upgrade.sql" "%LAPLACE_DEPLOY%\share\extension\%1--%EXT_INSTALLED%--%EXT_AVAILABLE%.sql" >nul || exit /b 1
set "PGOPTIONS=-c lock_timeout=20s"
psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -v ON_ERROR_STOP=1 -c "ALTER EXTENSION %1 UPDATE TO '%EXT_AVAILABLE%'"
set "RC=%ERRORLEVEL%"
set "PGOPTIONS="
if not "%RC%"=="0" exit /b 1
:bridge_done
for /f "usebackq delims=" %%v in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT extversion FROM pg_extension WHERE extname='%1'"`) do echo migrate-db: %1 extversion=%%v
exit /b 0
