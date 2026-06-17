@echo off
setlocal EnableDelayedExpansion

if not defined LAPLACE_ROOT call "%~dp0env.cmd"
if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
if not defined LAPLACE_INGEST_WORKERS set "LAPLACE_INGEST_WORKERS=4"
if not defined LAPLACE_DECOMPOSE_WORKERS set "LAPLACE_DECOMPOSE_WORKERS=1"
if not defined LAPLACE_COPY_VALIDATE set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=1"
if not defined LAPLACE_LADDER_START set "LAPLACE_LADDER_START=floor"

cd /d "%LAPLACE_ROOT%\app"

if /i "%LAPLACE_LADDER_START%"=="proof" (
  echo ==== ladder start: proof path - floor + *Net cluster assumed present ====
  goto stage_proof
)

call :ingest unicode || exit /b 1
call :ingest iso639 || exit /b 1

if exist "!INGEST!\test-data\text" (
  call :ingest document "!INGEST!\test-data\text" || exit /b 1
) else (
  echo ==== [skip] test-data text annex not found: !INGEST!\test-data\text ====
)

for %%s in (wordnet omw verbnet propbank framenet semlink conceptnet atomic2020 ud wiktionary) do (
  call :ingest %%s || exit /b 1
)
if /i "%LAPLACE_LADDER_STOP%"=="nets" goto ladder_done

:stage_proof
if "%LAPLACE_SKIP_USAGE%"=="1" (
  echo ==== [skip] tatoeba + opensubtitles -- LAPLACE_SKIP_USAGE=1 ====
) else (
  call :ingest tatoeba || exit /b 1
  call :ingest opensubtitles || exit /b 1
)
if /i "%LAPLACE_LADDER_STOP%"=="usage" goto ladder_done

if exist "!INGEST!\stack-v2" (
  call :ingest stack "!INGEST!\stack-v2" || exit /b 1
) else (
  echo ==== [skip] stack-v2 -- run scripts\win\download-code-data.cmd stack-v2 ====
)
for %%r in (Laplace) do (
  if exist "!REPOS!\%%r" (
    call :ingest repo "!REPOS!\%%r" || exit /b 1
  ) else (
    echo ==== [skip] repo not found: !REPOS!\%%r ====
  )
)
if exist "!INGEST!\code-authority" (
  for /d %%a in ("!INGEST!\code-authority\*") do (
    call :ingest repo "%%a" || exit /b 1
  )
) else (
  echo ==== [skip] authority sources -- run scripts\win\download-code-data.cmd authority ====
)
if exist "!INGEST!\tiny-codes" (
  call :ingest tiny-codes "!INGEST!\tiny-codes" || exit /b 1
) else (
  echo ==== [skip] tiny-codes -- in D:\Models\hub (HF) ====
)

if "%LAPLACE_SKIP_MODELS%"=="1" (
  echo ==== [skip] safetensor snapshots -- LAPLACE_SKIP_MODELS=1 ====
  goto ladder_done
)
call :resolve_model LAPLACE_MODEL_TINYLLAMA LAPLACE_TINYLLAMA_DIR "models--TinyLlama--TinyLlama-1.1B-Chat-v1.0" TINYLLAMA
if errorlevel 1 exit /b 1
call :ingest safetensors "!TINYLLAMA!" || exit /b 1
call :resolve_model LAPLACE_MODEL_PHI LAPLACE_PHI2_DIR "models--microsoft--phi-2" PHI
if errorlevel 1 exit /b 1
call :ingest safetensors "!PHI!" || exit /b 1
call :resolve_model LAPLACE_MODEL_QWEN25_CODER LAPLACE_QWEN25_CODER_DIR "models--Qwen--Qwen2.5-Coder-3B-Instruct" QWEN
if errorlevel 1 exit /b 1
call :ingest safetensors "!QWEN!" || exit /b 1

:ladder_done
cd /d "%LAPLACE_ROOT%"
echo ==== LADDER COMPLETE ====
exit /b 0


:ingest
if "%LAPLACE_LADDER_DRY%"=="1" (
  echo ==== [dry] ingest %* ====
  exit /b 0
)
set "_saved_commit_rows=%LAPLACE_INGEST_COMMIT_ROWS%"
if /i "%~1"=="ud" if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=25000"
if /i "%~1"=="wiktionary" if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=50000"
echo ==== ingest %* ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest %*
if errorlevel 1 (
  if defined _saved_commit_rows (set "LAPLACE_INGEST_COMMIT_ROWS=%_saved_commit_rows%") else set "LAPLACE_INGEST_COMMIT_ROWS="
  set "_saved_commit_rows="
  exit /b 1
)
if defined _saved_commit_rows (set "LAPLACE_INGEST_COMMIT_ROWS=%_saved_commit_rows%") else set "LAPLACE_INGEST_COMMIT_ROWS="
set "_saved_commit_rows="
exit /b 0

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
  echo   need config.json + tokenizer.json + *.safetensors
  exit /b 1
)
set "_fam=%LAPLACE_MODEL_HUB%\%~3"
if not exist "!_fam!" (
  echo ERROR: model not found -- set %~1 to a snapshot dir, or download into:
  echo   !_fam!\snapshots\<rev>\
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
echo   set %~1 to your local HF snapshot path
exit /b 1
