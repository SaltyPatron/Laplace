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

rem laplace_geom and laplace_substrate have no upgrade path (pipeline.sh refuses the same way on
rem Linux): the version is a content hash, and a database that carries another version than the
rem host has is recreated (laplace-ops db substrate recreate -Yes), never updated in place.
call :current laplace_geom || exit /b 1
call :current laplace_substrate || exit /b 1
echo migrate-db: OK ^(schema generation verified: partitioned^)
exit /b 0
:current
set "EXT_INSTALLED=" & set "EXT_AVAILABLE="
for /f "usebackq delims=" %%v in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT extversion FROM pg_extension WHERE extname='%1'"`) do set "EXT_INSTALLED=%%v"
for /f "usebackq delims=" %%v in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT default_version FROM pg_available_extensions WHERE name='%1'"`) do set "EXT_AVAILABLE=%%v"
if not defined EXT_AVAILABLE ( echo ERROR: %1 is not available on %LAPLACE_PGHOST% & exit /b 1 )
if not defined EXT_INSTALLED exit /b 0
if not "%EXT_INSTALLED%"=="%EXT_AVAILABLE%" (
  echo ERROR: %1 %EXT_INSTALLED% is installed in %LAPLACE_DBNAME% but %EXT_AVAILABLE% is available.
  echo   Extensions have no upgrade path; recreate the substrate database: laplace-ops db substrate recreate -Yes
  exit /b 1
)
for /f "usebackq delims=" %%v in (`psql -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT extversion FROM pg_extension WHERE extname='%1'"`) do echo migrate-db: %1 extversion=%%v
exit /b 0
