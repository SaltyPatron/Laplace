@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

set "ROOT=%LAPLACE_ROOT%"
set "LAPLACE_WIN=%~dp0"

set "TEST_ONLY=0"
set "SKIP_PROMOTE=0"
set "FROM_SOURCE="
set "SINGLE_DB="
set "SINGLE_SOURCE="

:parse
if "%~1"=="" goto run
if /i "%~1"=="--test-only" set "TEST_ONLY=1" & shift & goto parse
if /i "%~1"=="--skip-promote" set "SKIP_PROMOTE=1" & shift & goto parse
if /i "%~1"=="--from" set "FROM_SOURCE=%~2" & shift & shift & goto parse
if /i "%~1"=="--db" (
  set "SINGLE_DB=%~2"
  shift
  shift
  goto parse
)
if not defined SINGLE_SOURCE (
  set "SINGLE_SOURCE=%~1"
  shift
  goto parse
)
echo ERROR: unknown arg '%~1'
exit /b 2

:run
if defined SINGLE_DB (
  if not defined SINGLE_SOURCE (
    echo ERROR: --db requires a source argument
    exit /b 2
  )
  echo ==== matrix single rerun: !SINGLE_SOURCE! on !SINGLE_DB! ====
  call "!LAPLACE_WIN!decomposer-test.cmd" "!SINGLE_SOURCE!" --db "!SINGLE_DB!" || exit /b 1
  if "!TEST_ONLY!"=="0" if "!SKIP_PROMOTE!"=="0" (
    call "!LAPLACE_WIN!decomposer-promote.cmd" "!SINGLE_SOURCE!" || exit /b 1
  )
  exit /b 0
)

if defined SINGLE_SOURCE (
  echo ==== matrix single: !SINGLE_SOURCE! ====
  call "!LAPLACE_WIN!decomposer-test.cmd" "!SINGLE_SOURCE!" || exit /b 1
  if "!TEST_ONLY!"=="0" if "!SKIP_PROMOTE!"=="0" (
    call "!LAPLACE_WIN!decomposer-promote.cmd" "!SINGLE_SOURCE!" || exit /b 1
  )
  exit /b 0
)

for /f "usebackq delims=" %%L in (`python -c "import json; o=json.load(open(r'!ROOT!\scripts\decomposer-gates.json'))['manifest_order']; print(' '.join(o))"`) do set "ORDER=%%L"

set "SKIP=0"
if defined FROM_SOURCE set "SKIP=1"

for %%s in (!ORDER!) do (
  if "!SKIP!"=="1" (
    if /i "%%s"=="!FROM_SOURCE!" set "SKIP=0"
    if "!SKIP!"=="1" (
      echo ==== matrix skip %%s ^(before --from !FROM_SOURCE!^) ====
    )
  )
  if "!SKIP!"=="0" (
    echo.
    echo ========================================
    echo ==== matrix: test %%s ====
    echo ========================================
    call "!LAPLACE_WIN!decomposer-test.cmd" %%s || (
      echo ERROR: matrix stopped at %%s — fix and resume with --from %%s
      exit /b 1
    )
    if "!TEST_ONLY!"=="0" if "!SKIP_PROMOTE!"=="0" (
      call "!LAPLACE_WIN!decomposer-promote.cmd" %%s || (
        echo ERROR: promote failed at %%s
        exit /b 1
      )
    ) else (
      echo ==== matrix: promote skipped for %%s ====
    )
  )
)

echo ==== DECOMPOSER-MATRIX COMPLETE ====
exit /b 0
