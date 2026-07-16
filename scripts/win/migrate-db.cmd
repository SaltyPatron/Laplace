@echo off
rem migrate-db — create + migrate %LAPLACE_DBNAME% on %LAPLACE_PGHOST% via Laplace.Migrations
rem (EnsureDatabase creates the DB; layer1 migration installs laplace_substrate from the
rem HOST-side available extension). Then gate on schema generation: post-PR#322 substrate
rem is two-axis partitioned — laplace.entities must be relkind 'p'. A plain 'r' means the
rem host's installed extension predates the greenfield schema and seeding would target the
rem wrong generation: stop and deploy first.
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

set "MIG=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Migrations\Release\net10.0\Laplace.Migrations.dll"
if not exist "%MIG%" (
  echo migrate-db: building Laplace.Migrations ^(dll missing^)
  dotnet build "%LAPLACE_ROOT%\app\Laplace.Migrations\Laplace.Migrations.csproj" -c Release -v q --nologo || exit /b 1
)

echo ==== migrate-db: %LAPLACE_PGHOST%/%LAPLACE_DBNAME% ====
dotnet "%MIG%" up || exit /b 1

set "RELKIND="
for /f "usebackq delims=" %%k in (`"%PGBIN%\psql.exe" -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='laplace' AND c.relname='entities'"`) do set "RELKIND=%%k"
if not "%RELKIND%"=="p" (
  echo ERROR: laplace.entities relkind='%RELKIND%' — expected 'p' ^(two-axis partitioned, PR#322^)
  echo   the extension installed on %LAPLACE_PGHOST% predates the greenfield schema.
  echo   Fix: push main, then run CI to rebuild+deploy the extension on the host:
  echo     gh workflow run laplace.yml --repo SaltyPatron/Laplace --ref main
  echo   then drop the stale DB there and rerun this script.
  exit /b 1
)

for /f "usebackq delims=" %%v in (`"%PGBIN%\psql.exe" -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -tAc "SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'"`) do echo migrate-db: laplace_substrate extversion=%%v
echo migrate-db: OK ^(schema generation verified: partitioned^)
exit /b 0
