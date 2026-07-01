@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
if not defined LAPLACE_DBNAME set "LAPLACE_DBNAME=laplace_chess_test"
set "PGDATABASE=%LAPLACE_DBNAME%"

echo ==== %PGDATABASE% %DATE% %TIME% ====
psql -h localhost -U postgres -d %PGDATABASE% -P pager=off -v ON_ERROR_STOP=1 -f "%~dp0sql\chess-test-status.sql"
exit /b %ERRORLEVEL%
