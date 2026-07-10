@echo off
rem Tony.bat — default local pipeline. Prefer the named hosts explicitly:
rem   Tony_Hart-Desktop.bat  → localhost Postgres + IIS
rem   Tony_Hart-Server.bat   → build here, seed against hart-server
cd /d "%~dp0"
call "%~dp0Tony_Hart-Desktop.bat" %*
exit /b %ERRORLEVEL%
