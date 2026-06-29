@echo off
call "%~dp0env.cmd" || exit /b 1
psql -U postgres -d laplace -v ON_ERROR_STOP=1 -f "%~dp0_apply-define-fix.sql"
