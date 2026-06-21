@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

if "%~1"=="" (
  echo usage: db-isolate.cmd ^<dbname^> [--recycle]
  echo   DROP + CREATE database with laplace extensions - parameterized db-reset.
  exit /b 2
)

set "DBNAME=%~1"
set "RECYCLE=0"
if /i "%~2"=="--recycle" set "RECYCLE=1"
if /i "%~1"=="--recycle" (
  set "RECYCLE=1"
  shift
  set "DBNAME=%~1"
)

if "%DBNAME%"=="" (
  echo ERROR: db-isolate requires a database name
  exit /b 2
)

cd /d "%LAPLACE_ROOT%"

echo ==== terminate backends on %DBNAME% ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='!DBNAME!' AND pid<>pg_backend_pid();" || exit /b 1

echo ==== DROP + recreate %DBNAME% ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS !DBNAME!;" || exit /b 1
"%PGBIN%\createdb.exe" -h localhost -U postgres !DBNAME! || exit /b 1

echo ==== install extensions on %DBNAME% ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d !DBNAME! -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || exit /b 1

echo ==== post-create identity health on %DBNAME% ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d !DBNAME! -P pager=off -v ON_ERROR_STOP=1 -c "SET search_path = laplace, public; SELECT * FROM substrate_health();" || exit /b 1

if "%RECYCLE%"=="1" (
  echo ==== recycle backends (fresh DLL load on next connection) ====
  call "%~dp0install-extensions.cmd" --recycle || exit /b 1
)

echo ==== DB-ISOLATE COMPLETE: %DBNAME% ====
exit /b 0
