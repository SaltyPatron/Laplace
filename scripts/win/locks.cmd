@echo off
setlocal
call "%~dp0env.cmd" || exit /b 1
set "KILL="
if /i "%~1"=="--kill" set "KILL=-Kill"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0locks.ps1" %KILL%
exit /b %ERRORLEVEL%
