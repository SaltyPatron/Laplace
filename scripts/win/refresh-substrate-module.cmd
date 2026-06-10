@echo off
setlocal
rem ==== Refresh one laplace_substrate SQL module on a live database ============
rem   scripts\win\refresh-substrate-module.cmd <NN_module.sql.in> <database>
rem e.g.
rem   scripts\win\refresh-substrate-module.cmd 26_generation.sql.in laplace_code
rem Modules are CREATE OR REPLACE, so this is idempotent. New databases get the
rem module automatically via CREATE EXTENSION (after install-extensions.cmd).
rem ==============================================================================
if "%~2"=="" (
    echo usage: refresh-substrate-module.cmd ^<NN_module.sql.in^> ^<database^>
    exit /b 2
)
call "%~dp0env.cmd"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gen-sql.ps1" -Module "%~1" -Database "%~2" || exit /b 1
