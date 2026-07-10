@echo off
rem Tony_Hart-Desktop — local Hart-Desktop pipeline (localhost Postgres + IIS).
rem Run from an elevated cmd.exe if you need tune-pg restart / deploy-api.
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "LAPLACE_PGHOST=localhost"
set "LAPLACE_ENV_LOADED="
set "OUT=D:\Data\Output"
if not exist "%OUT%" mkdir "%OUT%"

echo ===== Tony_Hart-Desktop started %DATE% %TIME% =====
echo target DB: %LAPLACE_PGHOST%
echo logs:      %OUT%\
echo.

call :run tune-pg || goto :fail
call :run build-cutechess || goto :fail
call :run build-deps || goto :fail
call :run rebuild-all || goto :fail
call :run build-engine-asan || goto :fail
call :run build-web || goto :fail
call :run install-extensions || goto :fail
call :run publish || goto :fail
call :run deploy-api || goto :fail
call :run db-reset || goto :fail
call :run seed-foundation || goto :fail

call :run_args seed-step "document" "D:\Data\Ingest\test-data\text" documents || goto :fail
call :run_args seed-step "openings" "D:\Data\Ingest\Games\Chess\openings" openings || goto :fail
call :run_args seed-step "atomic2020" "" atomic2020 || goto :fail
call :run_args seed-step "omw" "" omw || goto :fail
call :run_args seed-step "conceptnet" "" conceptnet || goto :fail
call :run_args seed-step "ud" "" ud || goto :fail
call :run_args seed-step "chess" "D:\Data\Ingest\Games\Chess" chess-chesscom || goto :fail
call :run_args seed-step "chess" "D:\Data\Ingest\Games\Chess\Lumbras\otb" chess-otb || goto :fail
call :run_args seed-step "wiktionary" "" wiktionary || goto :fail
call :run_args seed-step "tatoeba" "" tatoeba || goto :fail
call :run_args seed-step "opensubtitles" "" opensubtitles || goto :fail

echo.
echo ===== Tony_Hart-Desktop OK %DATE% %TIME% =====
exit /b 0

:fail
echo.
echo ===== Tony_Hart-Desktop FAILED at !LAST_STEP! — see %OUT%\!LAST_LOG!.log =====
exit /b 1

:run
set "LAST_STEP=%~1"
set "LAST_LOG=%~1"
echo ==== %~1 ====
cmd /c "call scripts\win\%~1.cmd" > "%OUT%\%~1.log" 2>&1
if errorlevel 1 (
  echo FAILED %~1 — %OUT%\%~1.log
  exit /b 1
)
echo OK %~1
exit /b 0

:run_args
rem %1=script  %2=arg1  %3=arg2  %4=log-name
set "LAST_STEP=%~1 %~2"
set "LAST_LOG=%~4"
echo ==== %~1 %~2 ====
if "%~3"=="" (
  cmd /c "call scripts\win\%~1.cmd %~2" > "%OUT%\%~4.log" 2>&1
) else (
  cmd /c "call scripts\win\%~1.cmd %~2 \"%~3\"" > "%OUT%\%~4.log" 2>&1
)
if errorlevel 1 (
  echo FAILED %~1 %~2 — %OUT%\%~4.log
  exit /b 1
)
echo OK %~1 %~2
exit /b 0
