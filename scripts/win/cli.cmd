@echo off
setlocal
call "%~dp0env.cmd"
"%LAPLACE_ROOT%\app\Laplace.Cli\bin\Release\net10.0\Laplace.Cli.exe" %*
exit /b %ERRORLEVEL%
