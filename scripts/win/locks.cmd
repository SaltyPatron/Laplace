@echo off
setlocal
rem Who holds Laplace artifacts (build outputs, app bins, deployed extension DLLs).
rem Usage: locks.cmd [--kill]   (--kill stops the safe holders: CLI/endpoint/test hosts; never postgres or live builds)
set "KILL="
if /i "%~1"=="--kill" set "KILL=-Kill"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0locks.ps1" %KILL%
exit /b %ERRORLEVEL%
