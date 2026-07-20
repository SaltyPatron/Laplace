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

rem Durable session timeline: per-step START/OK/FAILED survives a torn-down console
rem so a mid-run crash still tells you WHICH step died (and thus where to resume).
set "SESSIONLOG=%OUT%\_session-desktop.log"
> "%SESSIONLOG%" echo ===== Tony_Hart-Desktop session %DATE% %TIME% =====

rem Suppress the WER modal crash dialog for this run. On a fault (incl. this box's
rem known 14900KS cross-process AVs) the child returns its NTSTATUS to the tee
rem instead of hanging on a modal box that, when dismissed, tears down this
rem elevated console before :fail can log the step. Restored at every exit.
call :wer_off

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

rem ---- Clean DB / seed foundation (uncomment as needed) ----
call :tee db-reset "db-reset.cmd" || goto :fail
rem call :tee seed-foundation "seed-foundation.cmd" || goto :fail
call :tee seed-unicode "seed-step.cmd unicode" || goto :fail
call :tee seed-iso "seed-step.cmd iso639" || goto :fail
call :tee seed-cili "seed-step.cmd cili" || goto :fail
call :tee seed-semlink "seed-step.cmd semlink" || goto :fail
call :tee seed-propbank "seed-step.cmd propbank" || goto :fail
call :tee seed-wordnet "seed-step.cmd wordnet" || goto :fail
call :tee seed-verbnet "seed-step.cmd verbnet" || goto :fail
call :tee seed-framenet "seed-step.cmd framenet" || goto :fail
call :tee seed-mapnet "seed-step.cmd mapnet" || goto :fail
call :tee seed-wordframenet "seed-step.cmd wordframenet" || goto :fail
call :tee seed-omw "seed-step.cmd omw" || goto :fail
call :tee seed-atomic2020 "seed-step.cmd atomic2020" || goto :fail
rem call :tee seed-ud "seed-step.cmd ud" || goto :fail
rem call :tee seed-conceptnet "seed-step.cmd conceptnet" || goto :fail
rem call :tee seed-wiktionary "seed-step.cmd wiktionary" || goto :fail
rem call :tee seed-tatoeba "seed-step.cmd tatoeba" || goto :fail
rem call :tee seed-opensubtitles "seed-step.cmd opensubtitles" || goto :fail

rem --- Template ---
rem call :tee seed- "seed-step.cmd " || goto :fail

rem ---- Optionals ----

call :tee seed-documents "seed-step.cmd document \"D:\Data\Ingest\test-data\text\"" || goto :fail
call :tee seed-chess-books "seed-step.cmd chess-books \"D:\Data\Ingest\test-data\text\"" || goto :fail
call :tee seed-chess-magnus "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\MagnusCarlsen_chesscom.pgn\"" || goto :fail
rem call :tee seed-chess-hikaru "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\Hikaru_chesscom.pgn\"" || goto :fail
call :tee seed-chess-fabiano "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\FabianoCaruana_chesscom.pgn\"" || goto :fail
call :tee seed-chess-anthony "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\Anthony-Hart_chesscom.pgn\"" || goto :fail
call :tee seed-chess-games "seed-step.cmd chess \"C:\Users\ahart\Downloads\games.pgn\"" || goto :fail
call :tee seed-chess-games1 "seed-step.cmd chess \"C:\Users\ahart\Downloads\games(1).pgn\"" || goto :fail

echo.
echo ===== Tony_Hart-Desktop / HART-DESKTOP OK %DATE% %TIME% =====
>> "%SESSIONLOG%" echo ===== SESSION OK %DATE% %TIME% =====
call :wer_restore
pause
exit /b 0

:fail
echo.
echo ===== Tony_Hart-Desktop FAILED at !LAST_STEP! - see %OUT%\!LAST_LOG!.log =====
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
