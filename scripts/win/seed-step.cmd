@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

if "%~1"=="" goto usage
if /i "%~1"=="help" goto usage
if /i "%~1"=="--list" goto list

set "STEP=%~1"
set "EXTRA="
if not "%~2"=="" set "EXTRA=%~2"
if not "%~3"=="" set "EXTRA=%EXTRA% %~3"
if not "%~4"=="" set "EXTRA=%EXTRA% %~4"
if not "%~5"=="" set "EXTRA=%EXTRA% %~5"
if not "%~6"=="" set "EXTRA=%EXTRA% %~6"
if not "%~7"=="" set "EXTRA=%EXTRA% %~7"
if not "%~8"=="" set "EXTRA=%EXTRA% %~8"
if not "%~9"=="" set "EXTRA=%EXTRA% %~9"

if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
if not defined LAPLACE_DECOMPOSE_WORKERS set "LAPLACE_DECOMPOSE_WORKERS=1"
rem COPY-blob re-validation is a debug-only detector for the native AV/heap-corruption bug
rem that was root-caused and fixed (commits 87eeee3 vertices-not-doubles, 97b58e9 walk header).
rem It is read-only (can only detect, never prevent/cause) and costs a full blob copy+field-walk
rem per COPY on the hot path. Default off; set LAPLACE_COPY_VALIDATE=1 to re-arm for debugging.
if not defined LAPLACE_COPY_VALIDATE set "LAPLACE_COPY_VALIDATE=0"

cd /d "%LAPLACE_ROOT%\app"

if /i "%STEP%"=="unicode"       goto run_ingest
if /i "%STEP%"=="iso639"        goto run_ingest
if /i "%STEP%"=="wordnet"       goto run_ingest
if /i "%STEP%"=="omw"           goto run_ingest
if /i "%STEP%"=="verbnet"       goto run_ingest
if /i "%STEP%"=="propbank"      goto run_ingest
if /i "%STEP%"=="framenet"      goto run_ingest
if /i "%STEP%"=="semlink"       goto run_ingest
if /i "%STEP%"=="mapnet"        goto run_ingest
if /i "%STEP%"=="wordframenet" goto run_ingest
if /i "%STEP%"=="conceptnet"    goto run_ingest_ud_commit
if /i "%STEP%"=="atomic2020"    goto run_ingest
if /i "%STEP%"=="ud"            goto run_ingest_ud_commit
if /i "%STEP%"=="wiktionary"    goto run_ingest_wikt_commit
if /i "%STEP%"=="tatoeba"       goto run_ingest
if /i "%STEP%"=="opensubtitles" goto run_ingest
if /i "%STEP%"=="document"      goto run_ingest_path
if /i "%STEP%"=="stack"         goto run_ingest_path
if /i "%STEP%"=="repo"          goto run_ingest_path
if /i "%STEP%"=="tiny-codes"    goto run_ingest_path
if /i "%STEP%"=="model-tinyllama" goto run_model_tinyllama
if /i "%STEP%"=="model-phi"      goto run_model_phi
if /i "%STEP%"=="model-qwen"     goto run_model_qwen
if /i "%STEP%"=="safetensors"    goto run_ingest_path

echo ERROR: unknown seed step '%STEP%'
goto usage

:run_ingest
call :run_ingest_impl
exit /b %ERRORLEVEL%

:run_ingest_ud_commit
set "_saved=%LAPLACE_INGEST_COMMIT_ROWS%"
if /i "%STEP%"=="ud" if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=25000"
if /i "%STEP%"=="conceptnet" (
  rem Unordered parallel commit: N workers commit self-contained batches concurrently.
  rem Smaller commit batches = better parallel granularity (measured GiST scaling peaks ~8 workers).
  if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=500000"
  if not defined LAPLACE_INGEST_BATCH set "LAPLACE_INGEST_BATCH=65536"
  if not defined LAPLACE_INGEST_COMPOSE_WORKERS set "LAPLACE_INGEST_COMPOSE_WORKERS=4"
  if not defined LAPLACE_INGEST_WORKERS set "LAPLACE_INGEST_WORKERS=8"
)
call :run_ingest_impl
set "RC=%ERRORLEVEL%"
if defined _saved (set "LAPLACE_INGEST_COMMIT_ROWS=%_saved%") else set "LAPLACE_INGEST_COMMIT_ROWS="
exit /b %RC%

:run_ingest_wikt_commit
set "_saved=%LAPLACE_INGEST_COMMIT_ROWS%"
if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=50000"
call :run_ingest_impl
set "RC=%ERRORLEVEL%"
if defined _saved (set "LAPLACE_INGEST_COMMIT_ROWS=%_saved%") else set "LAPLACE_INGEST_COMMIT_ROWS="
exit /b %RC%

:run_ingest_path
if "%EXTRA%"=="" (
  echo ERROR: seed-step %STEP% requires a path argument
  exit /b 2
)
call :run_ingest_impl
exit /b %ERRORLEVEL%

:run_ingest_impl
echo ==== seed-step: ingest %STEP% %EXTRA% ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest %STEP% %EXTRA%
exit /b %ERRORLEVEL%

:run_model_tinyllama
call :resolve_model LAPLACE_MODEL_TINYLLAMA LAPLACE_TINYLLAMA_DIR "models--TinyLlama--TinyLlama-1.1B-Chat-v1.0" TINYLLAMA || exit /b 1
set "EXTRA=!TINYLLAMA!"
set "STEP=safetensors"
goto run_ingest_path

:run_model_phi
call :resolve_model LAPLACE_MODEL_PHI LAPLACE_PHI2_DIR "models--microsoft--phi-2" PHI || exit /b 1
set "EXTRA=!PHI!"
set "STEP=safetensors"
goto run_ingest_path

:run_model_qwen
call :resolve_model LAPLACE_MODEL_QWEN25_CODER LAPLACE_QWEN25_CODER_DIR "models--Qwen--Qwen2.5-Coder-3B-Instruct" QWEN || exit /b 1
set "EXTRA=!QWEN!"
set "STEP=safetensors"
goto run_ingest_path

:resolve_model
set "%~4="
set "_resolved="
call set "_resolved=%%%~1%%"
if not defined _resolved call set "_resolved=%%%~2%%"
if defined _resolved (
  if exist "!_resolved!\config.json" if exist "!_resolved!\tokenizer.json" (
    set "%~4=!_resolved!"
    echo resolved %~4: !_resolved!
    exit /b 0
  )
  echo ERROR: %~1 is set but not a complete HF snapshot: !_resolved!
  exit /b 1
)
set "_fam=%LAPLACE_MODEL_HUB%\%~3"
if not exist "!_fam!" (
  echo ERROR: model not found — set %~1 or download into !_fam!\snapshots\<rev>\
  exit /b 1
)
for /d %%s in ("!_fam!\snapshots\*") do (
  if exist "%%s\config.json" if exist "%%s\tokenizer.json" (
    dir /b "%%s\*.safetensors" >nul 2>&1
    if not errorlevel 1 (
      set "%~4=%%s"
      echo resolved %~4: %%s
      exit /b 0
    )
  )
)
echo ERROR: no weighted snapshot under !_fam!\snapshots\
exit /b 1

:list
echo seed-step commands (run one at a time):
echo   floor:     unicode  iso639
echo   document:  document ^<path^>
echo   knowledge: wordnet  omw  verbnet  propbank  framenet  mapnet  wordframenet  semlink  conceptnet  atomic2020  ud  wiktionary
echo   usage:     tatoeba  opensubtitles
echo   code:      stack ^<path^>  repo ^<path^>  tiny-codes ^<path^>
echo   models:    model-tinyllama  model-phi  model-qwen  safetensors ^<snapshot-dir^>
echo.
echo stages: seed-stage floor ^| document ^| knowledge ^| usage ^| code ^| models
exit /b 0

:usage
echo usage: seed-step.cmd ^<step^> [path]
echo        seed-step.cmd --list
echo.
echo examples:
echo   seed-step.cmd unicode
echo   seed-step.cmd wordnet
echo   seed-step.cmd document "%INGEST%\test-data\text"
echo   seed-step.cmd repo "%REPOS%\Laplace"
exit /b 2
