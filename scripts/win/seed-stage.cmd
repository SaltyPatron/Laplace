@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

if "%~1"=="" goto usage

set "STAGE=%~1"
set "SCRIPTS=%~dp0"

if /i "%STAGE%"=="floor" goto stage_floor
if /i "%STAGE%"=="document" goto stage_document
if /i "%STAGE%"=="knowledge" goto stage_knowledge
if /i "%STAGE%"=="usage" goto stage_usage
if /i "%STAGE%"=="code" goto stage_code
if /i "%STAGE%"=="models" goto stage_models
if /i "%STAGE%"=="all" goto stage_all

echo ERROR: unknown stage '%STAGE%'
goto usage

:require_path
if exist "%~1" exit /b 0
echo ERROR: required path missing for seed stage '%STAGE%':
echo   %~1
echo %~2
exit /b 1

:stage_floor
call "%SCRIPTS%seed-step.cmd" unicode || exit /b 1
call "%SCRIPTS%seed-step.cmd" iso639 || exit /b 1
exit /b 0

:stage_document
call :require_path "!INGEST!\test-data\text" "  acquire test corpus under !INGEST!\test-data\text" || exit /b 1
call "%SCRIPTS%seed-step.cmd" document "!INGEST!\test-data\text" || exit /b 1
exit /b 0

:stage_knowledge
for %%s in (wordnet omw verbnet propbank framenet mapnet wordframenet semlink conceptnet atomic2020 ud wiktionary) do (
  call "%SCRIPTS%seed-step.cmd" %%s || exit /b 1
)
exit /b 0

:stage_usage
if "%LAPLACE_SKIP_USAGE%"=="1" (
  echo ERROR: LAPLACE_SKIP_USAGE=1 — usage stage is required; unset or set to 0
  exit /b 1
)
call :require_path "!INGEST!\Tatoeba" "  acquire Tatoeba under !INGEST!\Tatoeba" || exit /b 1
call :require_path "!INGEST!\OpenSubtitles" "  acquire OpenSubtitles under !INGEST!\OpenSubtitles" || exit /b 1
call "%SCRIPTS%seed-step.cmd" tatoeba || exit /b 1
call "%SCRIPTS%seed-step.cmd" opensubtitles || exit /b 1
exit /b 0

:stage_code
call :require_path "!INGEST!\stack-v2" "  run scripts\win\download-code-data.cmd stack-v2" || exit /b 1
call "%SCRIPTS%seed-step.cmd" stack "!INGEST!\stack-v2" || exit /b 1
call :require_path "!REPOS!\Laplace" "  clone Laplace repo to !REPOS!\Laplace" || exit /b 1
call "%SCRIPTS%seed-step.cmd" repo "!REPOS!\Laplace" || exit /b 1
call :require_path "!INGEST!\code-authority" "  run scripts\win\download-code-data.cmd authority" || exit /b 1
for /d %%a in ("!INGEST!\code-authority\*") do (
  call "%SCRIPTS%seed-step.cmd" repo "%%a" || exit /b 1
)
if exist "!INGEST!\tiny-codes" (
  set "_TINY=!INGEST!\tiny-codes"
) else if exist "!LAPLACE_MODEL_HUB!\datasets--nuprl--AgentPack\snapshots" (
  for /d %%s in ("!LAPLACE_MODEL_HUB!\datasets--nuprl--AgentPack\snapshots\*") do set "_TINY=%%s"
)
if not defined _TINY (
  echo ERROR: tiny-codes not found — run scripts\win\download-code-data.cmd tiny-codes
  echo   expected under !INGEST!\tiny-codes or HF hub datasets--nuprl--AgentPack
  exit /b 1
)
call "%SCRIPTS%seed-step.cmd" tiny-codes "!_TINY!" || exit /b 1
set "_TINY="
exit /b 0

:stage_models
if "%LAPLACE_SKIP_MODELS%"=="1" (
  echo ERROR: LAPLACE_SKIP_MODELS=1 — models stage is required; unset or set to 0
  exit /b 1
)
call "%SCRIPTS%seed-step.cmd" model-tinyllama || exit /b 1
call "%SCRIPTS%seed-step.cmd" model-phi || exit /b 1
call "%SCRIPTS%seed-step.cmd" model-qwen || exit /b 1
exit /b 0

:stage_all
call "%SCRIPTS%seed-stage.cmd" floor || exit /b 1
call "%SCRIPTS%seed-stage.cmd" document || exit /b 1
call "%SCRIPTS%seed-stage.cmd" knowledge || exit /b 1
call "%SCRIPTS%seed-stage.cmd" usage || exit /b 1
call "%SCRIPTS%seed-stage.cmd" code || exit /b 1
call "%SCRIPTS%seed-stage.cmd" models || exit /b 1
exit /b 0

:usage
echo usage: seed-stage.cmd ^<floor^|document^|knowledge^|usage^|code^|models^|all^>
exit /b 2
