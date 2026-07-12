@echo off
rem Tony_Hart-Server — local build + seed against host target HART-SERVER (remote PG).
rem Naming: Tony_Hart-<Target>  (_ joins operator prefix; - joins host target).
rem No local install-extensions / db-reset / deploy-api — those belong on HART-SERVER.
rem Extensions must already be live on the remote. Logs: D:\Data\Output\<step>.log
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "LAPLACE_PGHOST=hart-server"
rem Force env.cmd to rebuild LAPLACE_DB from the host above.
set "LAPLACE_DB="
set "LAPLACE_ENV_LOADED="
set "OUT=D:\Data\Output"
if not exist "%OUT%" mkdir "%OUT%"

rem Optional override: Tony_Hart-Server.bat [rebuild-all modules...]
rem Default: native + app (no local install / no IIS). Publish stages artifacts only.
set "MODULES=%*"
if not defined MODULES set "MODULES=native app"

echo ===== Tony_Hart-Server / HART-SERVER started %DATE% %TIME% =====
echo target:    HART-SERVER  PGHOST=%LAPLACE_PGHOST%  (remote — seeds only)
echo modules:   %MODULES%
echo logs:      %OUT%\
echo.

call :tee build-cutechess "build-cutechess.cmd" || goto :fail
call :tee rebuild-all "rebuild-all.cmd %MODULES%" || goto :fail
call :tee build-engine-asan "build-engine-asan.cmd" || goto :fail
rem Stage endpoint/UCI/web locally — do NOT deploy-api (IIS is HART-DESKTOP / host-local).
call :tee publish "publish.cmd" || goto :fail

echo ==== skip install-extensions / deploy-api / db-reset (not for HART-SERVER from this box) ====

call :tee seed-foundation "seed-foundation.cmd" || goto :fail
call :tee seed-documents "seed-step.cmd document \"D:\Data\Ingest\test-data\text\"" || goto :fail
call :tee seed-openings "seed-step.cmd openings \"D:\Data\Ingest\Games\Chess\openings\"" || goto :fail
call :tee seed-atomic2020 "seed-step.cmd atomic2020" || goto :fail
call :tee seed-omw "seed-step.cmd omw" || goto :fail
call :tee seed-conceptnet "seed-step.cmd conceptnet" || goto :fail
call :tee seed-ud "seed-step.cmd ud" || goto :fail
call :tee seed-chesscom "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\"" || goto :fail
call :tee seed-chess-otb "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\Lumbras\otb\"" || goto :fail
call :tee seed-wiktionary "seed-step.cmd wiktionary" || goto :fail
call :tee seed-tatoeba "seed-step.cmd tatoeba" || goto :fail
call :tee seed-opensubtitles "seed-step.cmd opensubtitles" || goto :fail

echo.
echo ===== Tony_Hart-Server / HART-SERVER OK %DATE% %TIME% =====
pause
exit /b 0

:fail
echo.
echo ===== Tony_Hart-Server FAILED at !LAST_STEP! - see %OUT%\!LAST_LOG!.log =====
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
