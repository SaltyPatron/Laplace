@echo off
rem audit-substrate — run scripts\sql\substrate-audit.sql against %LAPLACE_PGHOST%/%LAPLACE_DBNAME%.
rem Same audit seed-everything runs, but host/db/user come from env so it works remote.
setlocal
call "%~dp0env.cmd"
echo ==== audit-substrate: %LAPLACE_PGHOST%/%LAPLACE_DBNAME% ====
"%PGBIN%\psql.exe" -h %LAPLACE_PGHOST% -U %LAPLACE_PGUSER% -d %LAPLACE_DBNAME% -P pager=off -v ON_ERROR_STOP=1 -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql"
exit /b %ERRORLEVEL%
