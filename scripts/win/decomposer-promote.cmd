@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

if "%~1"=="" goto usage

set "SOURCE=%~1"
set "ROOT=%LAPLACE_ROOT%"

if not defined LAPLACE_CANONICAL_DB set "LAPLACE_CANONICAL_DB=laplace"
if not defined LAPLACE_ISOLATE_PREFIX set "LAPLACE_ISOLATE_PREFIX=laplace_d"

set "MARKER_DIR=!ROOT!\.ingest-proof\promoted"
if not exist "!MARKER_DIR!" mkdir "!MARKER_DIR!"

set "REPORT=!ROOT!\.ingest-proof\decomposer-!SOURCE!.json"
if exist "!REPORT!" (
  python -c "import json,sys; r=json.load(open(r'!REPORT!')); sys.exit(0 if r.get('passed') else 1)"
  if errorlevel 1 (
    echo ERROR: gate report exists but failed: !REPORT!
    echo   re-run: decomposer-test.cmd !SOURCE!
    exit /b 1
  )
) else (
  echo WARN: no gate report at !REPORT! — proceeding anyway
)

set "_saved_db=!LAPLACE_DBNAME!"
set "_saved_conn=!LAPLACE_DB!"
set "LAPLACE_DBNAME=!LAPLACE_CANONICAL_DB!"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=!LAPLACE_CANONICAL_DB!"

echo ==== promote !SOURCE! into !LAPLACE_CANONICAL_DB! ====

if /i "!SOURCE!"=="document" (
  if not exist "!INGEST!\test-data\text" (
    echo ERROR: document promote requires !INGEST!\test-data\text
    exit /b 1
  )
  call "%~dp0seed-step.cmd" document "!INGEST!\test-data\text" || exit /b 1
) else (
  call "%~dp0seed-step.cmd" "!SOURCE!" || exit /b 1
)

echo promoted_at=!DATE! !TIME!> "!MARKER_DIR!\!SOURCE!.marker"
echo db=!LAPLACE_CANONICAL_DB!>> "!MARKER_DIR!\!SOURCE!.marker"

set "LAPLACE_DBNAME=!_saved_db!"
if defined _saved_conn (set "LAPLACE_DB=!_saved_conn!") else set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=!LAPLACE_DBNAME!"

echo ==== DECOMPOSER-PROMOTE OK: !SOURCE! -^> !LAPLACE_CANONICAL_DB! ====
exit /b 0

:usage
echo usage: decomposer-promote.cmd ^<source^>
echo   Re-runs ingest on laplace after isolated gates pass. Not a pg clone.
exit /b 2
