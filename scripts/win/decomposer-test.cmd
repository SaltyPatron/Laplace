@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

set "LAPLACE_WIN=%~dp0"

if "%~1"=="" goto usage

set "SOURCE=%~1"
set "SKIP_GATES=0"
set "CUSTOM_DB="
shift

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--skip-gates" set "SKIP_GATES=1" & shift & goto parse_args
if /i "%~1"=="--db" (
  set "CUSTOM_DB=%~2"
  shift
  shift
  goto parse_args
)
echo ERROR: unknown flag '%~1'
exit /b 2

:args_done
if not defined LAPLACE_ISOLATE_PREFIX set "LAPLACE_ISOLATE_PREFIX=laplace_d"
if not defined LAPLACE_CANONICAL_DB set "LAPLACE_CANONICAL_DB=laplace"

set "ROOT=%LAPLACE_ROOT%"

if /i "%SOURCE%"=="help" goto usage
if /i "%SOURCE%"=="--help" goto usage

for /f "usebackq delims=" %%L in (`python "!ROOT!\scripts\decomposer-isolate-plan.py" --source "!SOURCE!" --prefix "!LAPLACE_ISOLATE_PREFIX!"`) do set "%%L"

if defined CUSTOM_DB set "TARGET_DB=!CUSTOM_DB!"

if "!TARGET_DB!"=="" (
  echo ERROR: could not resolve isolated DB name for '!SOURCE!'
  exit /b 2
)

echo ==== decomposer-test: !SOURCE! on !TARGET_DB! ====
if defined PREREQ_SOURCES echo ==== prerequisites: !PREREQ_SOURCES! ====

echo ==== fresh isolate: !TARGET_DB! ====
call "!LAPLACE_WIN!db-isolate.cmd" "!TARGET_DB!" || exit /b 1

set "_saved_db=!LAPLACE_DBNAME!"
set "_saved_conn=!LAPLACE_DB!"
set "LAPLACE_DBNAME=!TARGET_DB!"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=!TARGET_DB!"

if defined PREREQ_SOURCES (
  for %%p in (!PREREQ_SOURCES!) do (
    echo ==== prerequisite ingest: %%p ====
    if /i "%%p"=="document" (
      if not exist "!INGEST!\test-data\text" (
        echo ERROR: document prerequisite requires !INGEST!\test-data\text
        exit /b 1
      )
      call "!LAPLACE_WIN!seed-step.cmd" document "!INGEST!\test-data\text" || exit /b 1
    ) else (
      call "!LAPLACE_WIN!seed-step.cmd" %%p || exit /b 1
    )
  )
)

echo ==== target ingest: !SOURCE! ====
if /i "!SOURCE!"=="omw" (
  set "LAPLACE_INGEST_WORKERS=4"
  set "LAPLACE_INGEST_COMPOSE_WORKERS=1"
)
if /i "!SOURCE!"=="wiktionary" (
  set "LAPLACE_INGEST_WORKERS=1"
  set "LAPLACE_INGEST_COMMIT_ROWS=50000"
)
if /i "!SOURCE!"=="document" (
  if not exist "!INGEST!\test-data\text" (
    echo ERROR: document test requires !INGEST!\test-data\text
    exit /b 1
  )
  call "!LAPLACE_WIN!seed-step.cmd" document "!INGEST!\test-data\text" || exit /b 1
) else (
  call "!LAPLACE_WIN!seed-step.cmd" "!SOURCE!" || exit /b 1
)

if "!SKIP_GATES!"=="0" (
  echo ==== gate check !SOURCE! ====
  python "!ROOT!\scripts\decomposer-gate-check.py" --source "!SOURCE!" --dbname "!TARGET_DB!" || exit /b 1
)

set "LAPLACE_DBNAME=!_saved_db!"
if defined _saved_conn (set "LAPLACE_DB=!_saved_conn!") else set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=!LAPLACE_DBNAME!"

echo ==== DECOMPOSER-TEST OK: !SOURCE! on !TARGET_DB! ====
exit /b 0

:usage
echo usage: decomposer-test.cmd ^<source^> [--db ^<dbname^>] [--skip-gates]
echo.
echo Fresh isolated DB (laplace_d_^<source^>): prerequisite ingests, target ingest, gates.
echo No pg clone — prerequisites come from decomposer-gates.json.
echo.
echo examples:
echo   decomposer-test.cmd unicode
echo   decomposer-test.cmd wordnet
echo   decomposer-test.cmd omw
exit /b 2
