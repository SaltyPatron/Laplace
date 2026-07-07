@echo off
setlocal
call "%~dp0env.cmd"
"%LAPLACE_CLI_EXE%" %*
exit /b %ERRORLEVEL%
