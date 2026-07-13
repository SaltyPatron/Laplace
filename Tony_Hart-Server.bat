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

rem Durable session timeline: per-step START/OK/FAILED survives a torn-down console
rem so a mid-run crash still tells you WHICH step died (and thus where to resume).
set "SESSIONLOG=%OUT%\_session-server.log"
> "%SESSIONLOG%" echo ===== Tony_Hart-Server session %DATE% %TIME% =====

rem Suppress the WER modal crash dialog for this run so a fault returns its NTSTATUS
rem to the tee (clean :fail + logged step) instead of hanging on a modal box.
rem Restored at every exit.
call :wer_off

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
rem ---- personal chess.com PGNs (unrem one/more; chess.com export max=1000) ----
rem call :tee seed-chess-anthony "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\Anthony-Hart_chesscom.pgn\"" || goto :fail
rem call :tee seed-chess-games "seed-step.cmd chess \"C:\Users\ahart\Downloads\games.pgn\"" || goto :fail
rem call :tee seed-chess-games1 "seed-step.cmd chess \"C:\Users\ahart\Downloads\games(1).pgn\"" || goto :fail
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
>> "%SESSIONLOG%" echo ===== SESSION OK %DATE% %TIME% =====
call :wer_restore
pause
exit /b 0

:fail
echo.
echo ===== Tony_Hart-Server FAILED at !LAST_STEP! - see %OUT%\!LAST_LOG!.log =====
>> "%SESSIONLOG%" echo ===== SESSION FAILED at !LAST_STEP! =====
call :wer_restore
pause
exit /b 1

:tee
rem %1=log stem  %2=command line under scripts\win\
set "LAST_LOG=%~1"
set "LAST_STEP=%~2"
echo ==== %~2 ====
echo log: %OUT%\%~1.log
>> "%SESSIONLOG%" echo [%TIME%] START %~2  (log: %~1.log)
del /q "%OUT%\%~1.log" >nul 2>&1
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\win\tee-run.ps1" ^
  -LogPath "%OUT%\%~1.log" ^
  -WorkingDirectory "%CD%" ^
  -CommandLine "call scripts\win\%~2"
set "STEP_RC=!ERRORLEVEL!"
if not "!STEP_RC!"=="0" (
  echo FAILED %~2 exit=!STEP_RC! - %OUT%\%~1.log
  >> "%SESSIONLOG%" echo [%TIME%] FAILED %~2 exit=!STEP_RC!
  exit /b 1
)
echo OK %~2
>> "%SESSIONLOG%" echo [%TIME%] OK %~2
exit /b 0

:wer_off
rem Save prior DontShowUI (if any) so we can restore the user's exact state.
set "WER_PREV="
for /f "tokens=3" %%v in ('reg query "HKCU\Software\Microsoft\Windows\Windows Error Reporting" /v DontShowUI 2^>nul ^| find "DontShowUI"') do set "WER_PREV=%%v"
reg add "HKCU\Software\Microsoft\Windows\Windows Error Reporting" /v DontShowUI /t REG_DWORD /d 1 /f >nul 2>&1
exit /b 0

:wer_restore
if defined WER_PREV (
  reg add "HKCU\Software\Microsoft\Windows\Windows Error Reporting" /v DontShowUI /t REG_DWORD /d %WER_PREV% /f >nul 2>&1
) else (
  reg delete "HKCU\Software\Microsoft\Windows\Windows Error Reporting" /v DontShowUI /f >nul 2>&1
)
exit /b 0
