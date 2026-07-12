@echo off
rem Tony_Hart-Desktop — elevated pipeline for host target HART-DESKTOP (localhost PG + IIS).
rem Naming: Tony_Hart-<Target>  (_ joins operator prefix; - joins host target).
rem Self-elevates for deploy-api / optional tune-pg. Logs: D:\Data\Output\<step>.log
setlocal EnableDelayedExpansion
cd /d "%~dp0"

net session >nul 2>&1
if errorlevel 1 (
  echo Elevating for HART-DESKTOP IIS deploy...
  powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "Start-Process -FilePath '%~f0' -WorkingDirectory '%~dp0.' -Verb RunAs"
  if errorlevel 1 (
    echo Elevation failed or denied.
    pause
    exit /b 1
  )
  exit /b 0
)

set "LAPLACE_PGHOST=localhost"
set "LAPLACE_ENV_LOADED="
set "OUT=D:\Data\Output"
if not exist "%OUT%" mkdir "%OUT%"

rem Optional override: Tony_Hart-Desktop.bat [rebuild-all modules...]
rem Examples:  (none) → ship   ^|  native  ^|  publish  ^|  engine extensions install
set "MODULES=%*"
if not defined MODULES set "MODULES=ship"

echo ===== Tony_Hart-Desktop / HART-DESKTOP started %DATE% %TIME% =====
echo target:    HART-DESKTOP  PGHOST=%LAPLACE_PGHOST%
echo modules:   %MODULES%
echo logs:      %OUT%\
echo.

rem call :tee tune-pg "tune-pg.cmd" || goto :fail
call :tee build-cutechess "build-cutechess.cmd" || goto :fail
call :tee rebuild-all "rebuild-all.cmd %MODULES%" || goto :fail
rem call :tee build-engine-asan "build-engine-asan.cmd" || goto :fail

rem ---- optional DB / seed campaigns (uncomment as needed) ----
call :tee db-reset "db-reset.cmd" || goto :fail
call :tee seed-foundation "seed-foundation.cmd" || goto :fail
call :tee seed-openings "seed-step.cmd openings \"D:\Data\Ingest\Games\Chess\openings\"" || goto :fail
rem ---- personal chess.com PGNs (unrem one/more; chess.com export max=1000) ----
rem call :tee seed-chess-anthony "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\Anthony-Hart_chesscom.pgn\"" || goto :fail
rem call :tee seed-chess-games "seed-step.cmd chess \"C:\Users\ahart\Downloads\games.pgn\"" || goto :fail
rem call :tee seed-chess-games1 "seed-step.cmd chess \"C:\Users\ahart\Downloads\games(1).pgn\"" || goto :fail
call :tee seed-documents "seed-step.cmd document \"D:\Data\Ingest\test-data\text\"" || goto :fail
call :tee seed-atomic2020 "seed-step.cmd atomic2020" || goto :fail
call :tee seed-omw "seed-step.cmd omw" || goto :fail
call :tee seed-conceptnet "seed-step.cmd conceptnet" || goto :fail
call :tee seed-ud "seed-step.cmd ud" || goto :fail
rem call :tee seed-wiktionary "seed-step.cmd wiktionary" || goto :fail
rem call :tee seed-tatoeba "seed-step.cmd tatoeba" || goto :fail
rem call :tee seed-opensubtitles "seed-step.cmd opensubtitles" || goto :fail

echo.
echo ===== Tony_Hart-Desktop / HART-DESKTOP OK %DATE% %TIME% =====
pause
exit /b 0

:fail
echo.
echo ===== Tony_Hart-Desktop FAILED at !LAST_STEP! - see %OUT%\!LAST_LOG!.log =====
pause
exit /b 1

:tee
rem %1=log stem  %2=command line under scripts\win\
set "LAST_LOG=%~1"
set "LAST_STEP=%~2"
echo ==== %~2 ====
echo log: %OUT%\%~1.log
del /q "%OUT%\%~1.log" >nul 2>&1
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\win\tee-run.ps1" ^
  -LogPath "%OUT%\%~1.log" ^
  -WorkingDirectory "%CD%" ^
  -CommandLine "call scripts\win\%~2"
if not "%ERRORLEVEL%"=="0" (
  echo FAILED %~2 exit=%ERRORLEVEL% - %OUT%\%~1.log
  exit /b 1
)
echo OK %~2
exit /b 0
