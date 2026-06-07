@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
ctest --test-dir build-win --output-on-failure %*
