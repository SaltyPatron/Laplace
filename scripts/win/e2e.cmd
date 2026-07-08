@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
if not defined LAPLACE_STAGING_THRESHOLD set "LAPLACE_STAGING_THRESHOLD=20000000"
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='%LAPLACE_DBNAME%'" | findstr 1 >nul || "%PGBIN%\createdb.exe" -h localhost -U postgres "%LAPLACE_DBNAME%" || exit /b 1
"%PGBIN%\psql.exe" -h localhost -U postgres -d "%LAPLACE_DBNAME%" -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || exit /b 1
cd app
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1
for %%s in (%*) do (
  echo ==== ingest %%s ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest %%s || exit /b 1
)
