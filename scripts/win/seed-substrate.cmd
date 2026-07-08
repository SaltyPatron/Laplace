@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_SKIP_MODELS set "LAPLACE_SKIP_MODELS=0"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=0"

echo ==== rebuild engine + extensions + deploy ====
call "%~dp0rebuild-all.cmd" || exit /b 1

echo ==== fresh database ====
call "%~dp0db-reset.cmd" || exit /b 1

call "%~dp0seed-ladder.cmd" || exit /b 1

echo ==== substrate audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1

echo ==== SEED-SUBSTRATE COMPLETE ====
exit /b 0
