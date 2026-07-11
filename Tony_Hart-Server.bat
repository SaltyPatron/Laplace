@echo off
rem Tony_Hart-Server — build locally, seed/verify against hart-server Postgres.
rem Does NOT install extensions, reset DB, or deploy IIS on this machine —
rem those are host-local. Extensions must already be live on hart-server.
rem Steps live-tee to console + D:\Data\Output\<step>.log (no silent redirects).
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "LAPLACE_PGHOST=hart-server"
rem Force env.cmd to rebuild LAPLACE_DB from the host above.
set "LAPLACE_DB="
set "LAPLACE_ENV_LOADED="
set "OUT=D:\Data\Output"
if not exist "%OUT%" mkdir "%OUT%"

echo ===== Tony_Hart-Server started %DATE% %TIME% =====
echo target DB: %LAPLACE_PGHOST%  (remote - seeds only)
echo logs:      %OUT%\  ^(live-teed to console^)
echo.

call :run build-cutechess || goto :fail
call :run build-deps || goto :fail
rem Build only — no local install-extensions / IIS. Target DB is hart-server.
call :run_args rebuild-all "--skip-install" "" rebuild-all || goto :fail
call :run build-engine-asan || goto :fail
call :run build-web || goto :fail
call :run publish || goto :fail

echo ==== skip install-extensions / deploy-api / db-reset (local-only; target is hart-server) ====

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
echo ===== Tony_Hart-Server OK %DATE% %TIME% =====
pause
exit /b 0

:fail
echo.
echo ===== Tony_Hart-Server FAILED at !LAST_STEP! - see %OUT%\!LAST_LOG!.log =====
pause
exit /b 1

:run
set "LAST_STEP=%~1"
set "LAST_LOG=%~1"
echo ==== %~1 ====
echo log: %OUT%\%~1.log
del /q "%OUT%\%~1.log" >nul 2>&1
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\win\tee-run.ps1" ^
  -LogPath "%OUT%\%~1.log" ^
  -WorkingDirectory "%CD%" ^
  -CommandLine "call scripts\win\%~1.cmd"
rem String compare, not `if errorlevel 1`: negative NTSTATUS exit codes
rem (crash 0xC0000005, Ctrl+C 0xC000013A) are < 1 and would read as success.
if not "%ERRORLEVEL%"=="0" (
  echo FAILED %~1 exit=%ERRORLEVEL% - %OUT%\%~1.log
  exit /b 1
)
echo OK %~1
exit /b 0

:run_args
rem %1=script  %2=arg1  %3=arg2  %4=log-name
set "LAST_STEP=%~1 %~2"
set "LAST_LOG=%~4"
echo ==== %~1 %~2 ====
echo log: %OUT%\%~4.log
del /q "%OUT%\%~4.log" >nul 2>&1
if "%~3"=="" (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\win\tee-run.ps1" ^
    -LogPath "%OUT%\%~4.log" ^
    -WorkingDirectory "%CD%" ^
    -CommandLine "call scripts\win\%~1.cmd %~2"
) else (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\win\tee-run.ps1" ^
    -LogPath "%OUT%\%~4.log" ^
    -WorkingDirectory "%CD%" ^
    -CommandLine "call scripts\win\%~1.cmd %~2 \"%~3\""
)
if not "%ERRORLEVEL%"=="0" (
  echo FAILED %~1 %~2 exit=%ERRORLEVEL% - %OUT%\%~4.log
  exit /b 1
)
echo OK %~1 %~2
exit /b 0
