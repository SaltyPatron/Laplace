@echo off
call "%~dp0env.cmd" || exit /b 1
call "%~dp0build-extensions.cmd" || exit /b 1
call "%~dp0install-extensions.cmd" || exit /b 1
psql -U postgres -d laplace -v ON_ERROR_STOP=1 -f "%~dp0_apply-lexical-peers-symmetric.sql"
psql -U postgres -d laplace -v ON_ERROR_STOP=1 -f "%~dp0_drop-lower-hack.sql"
call "%~dp0verify-lexical-peers-installed.cmd"
