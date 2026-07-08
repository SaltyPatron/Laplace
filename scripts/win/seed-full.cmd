@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "LAPLACE_SKIP_MODELS=1"
set "LAPLACE_INGEST_LANGS="
if not defined LAPLACE_COPY_VALIDATE set "LAPLACE_COPY_VALIDATE=0"

echo ==== FULL SEED FROM SCRATCH (all languages, no model deposition) ====
echo   LAPLACE_INGEST_LANGS=unset ^(all languages^)
echo   LAPLACE_SKIP_MODELS=%LAPLACE_SKIP_MODELS%

echo.
echo ===== STOP STALE CLI / BACKENDS =====
taskkill /F /IM Laplace.Cli.exe >nul 2>&1
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();" >nul 2>&1

echo.
echo ===== BUILD =====
call "%~dp0rebuild-all.cmd" || exit /b 1

echo.
echo ===== DB RESET =====
call "%~dp0db-reset.cmd" || exit /b 1

echo.
echo ===== SEED LADDER =====
call "%~dp0seed-ladder.cmd" || exit /b 1

echo.
echo ===== SUBSTRATE AUDIT =====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1

echo ==== SEED-FULL COMPLETE ====
exit /b 0
