@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "RECYCLE=0"
if /i "%~1"=="--recycle" set "RECYCLE=1"

echo ==== terminate laplace backends ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();" || exit /b 1

echo ==== DROP + recreate laplace ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS laplace;" || exit /b 1
"%PGBIN%\createdb.exe" -h localhost -U postgres laplace || exit /b 1

echo ==== deploy extension SQL + DLLs ====
rem --recycle is additive inside install-extensions (it only appends a backend
rem terminate after deploy + GUC wiring), so one call does the recycle work --
rem the previous second full install-extensions run for RECYCLE==1 duplicated
rem the entire deploy for one pg_terminate_backend's worth of work.
if "%RECYCLE%"=="1" (
  call "%~dp0install-extensions.cmd" --recycle || exit /b 1
) else (
  call "%~dp0install-extensions.cmd" || exit /b 1
)

echo ==== install extensions ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || exit /b 1

echo ==== post-create identity health ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -v ON_ERROR_STOP=1 -c "SET search_path = laplace, public; SELECT * FROM substrate_health();" || exit /b 1

echo ==== DB-RESET COMPLETE ====
exit /b 0
