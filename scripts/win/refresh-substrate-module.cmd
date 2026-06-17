@echo off
setlocal
if "%~2"=="" (
    echo usage: refresh-substrate-module.cmd ^<NN_module.sql.in^> ^<database^>
    exit /b 2
)
call "%~dp0env.cmd"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gen-sql.ps1" -Module "%~1" -Database "%~2" || exit /b 1
