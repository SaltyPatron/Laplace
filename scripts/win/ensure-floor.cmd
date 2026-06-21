@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

if not defined LAPLACE_ISOLATE_PREFIX set "LAPLACE_ISOLATE_PREFIX=laplace_d"
set "DB=%LAPLACE_DBNAME%"
if not defined DB set "DB=laplace"

for /f "usebackq delims=" %%u in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0seed-layer-check.ps1" -Key unicode -Src UnicodeDecomposer -Layer 0 -Dbname "!DB!"`) do set "%%u"
for /f "usebackq delims=" %%i in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0seed-layer-check.ps1" -Key iso639 -Src ISO639Decomposer -Layer 1 -Dbname "!DB!"`) do set "%%i"
if "!STAT_unicode!"=="t" if "!STAT_iso639!"=="t" (
  echo ==== floor layers OK on !DB! ====
  exit /b 0
)

echo ==== ensure-floor: unicode + iso639 on !DB! ====
set "_saved_db=!LAPLACE_DBNAME!"
set "_saved_conn=!LAPLACE_DB!"
set "LAPLACE_DBNAME=!DB!"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=!DB!"

call "%~dp0seed-step.cmd" unicode || exit /b 1
call "%~dp0seed-step.cmd" iso639 || exit /b 1

set "LAPLACE_DBNAME=!_saved_db!"
if defined _saved_conn (set "LAPLACE_DB=!_saved_conn!") else set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=!LAPLACE_DBNAME!"

echo ==== ENSURE-FLOOR COMPLETE: !DB! ====
exit /b 0
