@echo off
setlocal
call "%~dp0env.cmd" || exit /b 1
"%LAPLACE_CLI_EXE%" %*
exit /b %ERRORLEVEL%
