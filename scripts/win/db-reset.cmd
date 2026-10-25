@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd" || exit /b 1
cd /d "%LAPLACE_ROOT%"

rem db-reset.cmd [--recycle] --confirm <database>
rem DROPs and recreates %LAPLACE_DBNAME%. Destructive, so the target is never defaulted:
rem LAPLACE_DBNAME must be set, and the same name must be confirmed with --confirm <name>
rem (or LAPLACE_DB_RESET_CONFIRM=<name>) before anything connects to PostgreSQL.
set "RECYCLE=0"
set "CONFIRM=%LAPLACE_DB_RESET_CONFIRM%"
:parse_reset
if "%~1"=="" goto parse_reset_done
if /i "%~1"=="--recycle" ( set "RECYCLE=1" & shift /1 & goto parse_reset )
if /i "%~1"=="--confirm" ( set "CONFIRM=%~2" & shift /1 & shift /1 & goto parse_reset )
echo db-reset: unknown argument %~1
exit /b 2
:parse_reset_done
call "%~dp0require-dbname.cmd" || exit /b 2
echo ==== db-reset target: database "%LAPLACE_DBNAME%" on localhost ====
if not "%CONFIRM%"=="%LAPLACE_DBNAME%" (
  echo ERROR: db-reset drops "%LAPLACE_DBNAME%". Re-run with --confirm %LAPLACE_DBNAME% to proceed.
  exit /b 2
)

echo ==== terminate laplace backends ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='%LAPLACE_DBNAME%' AND pid<>pg_backend_pid();" || exit /b 1

echo ==== DROP + recreate %LAPLACE_DBNAME% ====
rem the name quoted (laplace-mono has a hyphen): cmd cannot nest quotes in -c, so the statement goes in from a file
set "RESET_SQL=%TEMP%\laplace-db-reset.sql"
> "%RESET_SQL%" echo DROP DATABASE IF EXISTS "%LAPLACE_DBNAME%";
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -f "%RESET_SQL%" || exit /b 1
"%PGBIN%\createdb.exe" -h localhost -U postgres "%LAPLACE_DBNAME%" || exit /b 1

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
"%PGBIN%\psql.exe" -h localhost -U postgres -d "%LAPLACE_DBNAME%" -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate CASCADE;" || exit /b 1

echo ==== post-create identity health ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d "%LAPLACE_DBNAME%" -P pager=off -v ON_ERROR_STOP=1 -c "SET search_path = laplace, public; SELECT * FROM substrate_health();" || exit /b 1

echo ==== declared role access ====
rem a recreated database has none of its roles' declared access: applied here, so a reset leaves it usable
pwsh -NoProfile -File "%~dp0apply-declared-access.ps1" -Database "%LAPLACE_DBNAME%" || exit /b 1

echo ==== DB-RESET COMPLETE ====
exit /b 0
