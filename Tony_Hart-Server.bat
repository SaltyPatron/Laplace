@echo off
rem Tony_Hart-Server — local build + remote migrate/seed against host target HART-SERVER.
rem Naming: Tony_Hart-<Target>  (_ joins operator prefix; - joins host target).
rem No local install-extensions / db-reset / deploy-api — those live on HART-SERVER (CI).
rem The remote extension must be current (CI deploy); migrate-db gates on the schema
rem generation and stops before seeding into a stale one. Logs: D:\Data\Output\<step>.log
setlocal EnableDelayedExpansion
cd /d "%~dp0"

rem HART-SERVER cluster: superuser is laplace_admin (initdb --username=laplace_admin);
rem there is NO postgres role on that host. Trust auth on 192.168.1.0/24.
set "LAPLACE_PGHOST=hart-server"
set "LAPLACE_PGUSER=laplace_admin"
rem Force env.cmd to rebuild LAPLACE_DB from the host/user above.
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
rem Default: native + app (decomposers/CLI/native DLLs; no local install / no IIS).
set "MODULES=%*"
if not defined MODULES set "MODULES=native app"

echo ===== Tony_Hart-Server / HART-SERVER started %DATE% %TIME% =====
echo target:    HART-SERVER  PGHOST=%LAPLACE_PGHOST%  PGUSER=%LAPLACE_PGUSER%  (remote — migrate + seeds)
echo modules:   %MODULES%
echo logs:      %OUT%\
echo.

rem ---- Phase 0: preflight (remote up, extension available, runner idle) ----
call :tee gate-remote "gate-remote.cmd" || goto :fail

rem ---- Phase 1: local build (client side of the wire: decomposers, CLI, native math) ----
call :tee rebuild-all "rebuild-all.cmd %MODULES%" || goto :fail
rem call :tee build-cutechess "build-cutechess.cmd" || goto :fail
rem call :tee build-engine-asan "build-engine-asan.cmd" || goto :fail
rem Stage endpoint/UCI/web locally only if wanted — never deploy-api from this box.
rem call :tee publish "publish.cmd" || goto :fail

rem ---- Phase 2: remote DB — create/migrate + schema-generation gate ----
call :tee migrate-db "migrate-db.cmd" || goto :fail

rem ---- Phase 3: floor ----
call :tee seed-unicode "seed-step.cmd unicode" || goto :fail
call :tee seed-iso639 "seed-step.cmd iso639" || goto :fail

rem ---- Phase 4: document ----
call :tee seed-documents "seed-step.cmd document \"D:\Data\Ingest\test-data\text\"" || goto :fail

rem ---- Phase 5: knowledge (canonical ladder order) ----
call :tee seed-cili "seed-step.cmd cili" || goto :fail
call :tee seed-wordnet "seed-step.cmd wordnet" || goto :fail
call :tee seed-omw "seed-step.cmd omw" || goto :fail
call :tee seed-verbnet "seed-step.cmd verbnet" || goto :fail
call :tee seed-propbank "seed-step.cmd propbank" || goto :fail
call :tee seed-framenet "seed-step.cmd framenet" || goto :fail
call :tee seed-mapnet "seed-step.cmd mapnet" || goto :fail
call :tee seed-wordframenet "seed-step.cmd wordframenet" || goto :fail
call :tee seed-semlink "seed-step.cmd semlink" || goto :fail
call :tee seed-conceptnet "seed-step.cmd conceptnet" || goto :fail
call :tee seed-atomic2020 "seed-step.cmd atomic2020" || goto :fail
call :tee seed-ud "seed-step.cmd ud" || goto :fail
call :tee seed-wiktionary "seed-step.cmd wiktionary" || goto :fail

rem ---- Phase 6: chess ----
call :tee seed-openings "seed-step.cmd openings \"D:\Data\Ingest\Games\Chess\openings\"" || goto :fail
call :tee seed-chess-books "seed-step.cmd chess-books \"D:\Data\Ingest\test-data\text\"" || goto :fail
call :tee seed-chess-anthony "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\Anthony-Hart_chesscom.pgn\"" || goto :fail
call :tee seed-chess-games "seed-step.cmd chess \"C:\Users\ahart\Downloads\games.pgn\"" || goto :fail
call :tee seed-chess-games1 "seed-step.cmd chess \"C:\Users\ahart\Downloads\games(1).pgn\"" || goto :fail
rem call :tee seed-chess-hikaru "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\Hikaru_chesscom.pgn\"" || goto :fail
rem call :tee seed-chess-fabiano "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\FabianoCaruana_chesscom.pgn\"" || goto :fail
call :tee seed-chesscom "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\"" || goto :fail
call :tee seed-chess-otb "seed-step.cmd chess \"D:\Data\Ingest\Games\Chess\Lumbras\otb\"" || goto :fail

rem ---- Phase 7: usage ----
call :tee seed-tatoeba "seed-step.cmd tatoeba" || goto :fail
call :tee seed-opensubtitles "seed-step.cmd opensubtitles" || goto :fail

rem ---- Phase 8: code (stage script owns path resolution: stack-v2, repos, tiny-codes) ----
call :tee seed-code "seed-stage.cmd code" || goto :fail

rem ---- Phase 9: models (model-lane SQL landed with the stage commit; deployed to
rem hart-server by CI run #325 — model_* functions verified present on the remote) ----
call :tee seed-model-tinyllama "seed-step.cmd model-tinyllama" || goto :fail
call :tee seed-model-phi "seed-step.cmd model-phi" || goto :fail
call :tee seed-model-qwen "seed-step.cmd model-qwen" || goto :fail

rem ---- Phase 10: audit ----
call :tee audit-substrate "audit-substrate.cmd" || goto :fail

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
