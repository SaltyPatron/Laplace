@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  seed-ladder.cmd -- THE witness ladder (executable mirror of witness-manifest.json)
rem ============================================================================
rem  Single source of truth for ingest ordering. Callers: e2e-master.cmd,
rem  seed-substrate.cmd, seed-resume-prove.cmd. Do NOT copy this ladder into a
rem  new orchestrator -- call it (lesson 2026-06-10: three divergent copies).
rem
rem  *Net link law (synset hub):
rem    WordNet  -- synsets + senses + lemma arena + pointer graph (anchor)
rem    OMW      -- same WordNetSynset IDs: translations/defs/examples per language
rem    VerbNet  -- lemma CORRESPONDS_TO wordnet/sense/{key}
rem    PropBank -- lemma HAS_SENSE roleset; roleset CORRESPONDS_TO verbnet/class
rem    FrameNet -- lemma EVOKES_FRAME framenet/frame
rem    SemLink  -- explicit propbank-verbnet and verbnet-framenet alignment
rem    ConceptNet / Atomic2020 -- en-scoped graphs sharing the lemma arena (deferred bulk)
rem
rem  Ordering law (manifest): floor -> *Net hub -> proof path -> models -> deferred lexical.
rem
rem  Contract (caller responsibilities):
rem    - env.cmd already called; target DB exists with extensions installed
rem    - Laplace.Cli already built Release (ladder uses dotnet run --no-build)
rem
rem  Knobs (all optional; value-checked, not defined-checked):
rem    LAPLACE_LADDER_START        floor (default) | proof  -- proof skips floor+nets (resume)
rem    LAPLACE_LADDER_DRY          1 = print the resolved plan, ingest nothing
rem    LAPLACE_SKIP_USAGE          1 (default) skips tatoeba/opensubtitles
rem    LAPLACE_SKIP_MODELS         1 skips safetensor deposits; UNSET/0 = models REQUIRED (error if missing)
rem    LAPLACE_SKIP_LEXICAL_BULK   1 skips conceptnet/atomic2020/ud/wiktionary (default 0)
rem    LAPLACE_MODEL_TINYLLAMA / LAPLACE_MODEL_PHI / LAPLACE_MODEL_QWEN25_CODER  snapshot dirs
rem ============================================================================

if not defined LAPLACE_ROOT call "%~dp0env.cmd"
rem Connection/data-path constants come from env.cmd (single source; pre-set to override).
if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
if not defined LAPLACE_INGEST_WORKERS set "LAPLACE_INGEST_WORKERS=4"
if not defined LAPLACE_DECOMPOSE_WORKERS set "LAPLACE_DECOMPOSE_WORKERS=1"
if not defined LAPLACE_COPY_VALIDATE set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=1"
if not defined LAPLACE_SKIP_LEXICAL_BULK set "LAPLACE_SKIP_LEXICAL_BULK=0"
if not defined LAPLACE_LADDER_START set "LAPLACE_LADDER_START=floor"

cd /d "%LAPLACE_ROOT%\app"

if /i "%LAPLACE_LADDER_START%"=="proof" (
  echo ==== ladder start: proof path - floor + *Net cluster assumed present ====
  goto stage_proof
)

rem ---- floor: L0-L1 ----------------------------------------------------------
call :ingest unicode || exit /b 1
call :ingest iso639 || exit /b 1

rem ---- *Net cluster (synset hub law) -----------------------------------------
for %%s in (wordnet omw verbnet propbank framenet semlink) do (
  call :ingest %%s || exit /b 1
)

:stage_proof
rem ---- proof path: code corpora ----------------------------------------------
if exist "!INGEST!\tiny-codes" (
  call :ingest tiny-codes "!INGEST!\tiny-codes" || exit /b 1
) else (
  echo ==== [skip] tiny-codes -- run scripts\win\download-code-data.cmd tiny-codes ====
)
if exist "!INGEST!\stack-v2" (
  call :ingest stack "!INGEST!\stack-v2" || exit /b 1
) else (
  echo ==== [skip] stack-v2 -- run scripts\win\download-code-data.cmd stack-v2 ====
)

rem ---- world usage (optional) -------------------------------------------------
if "%LAPLACE_SKIP_USAGE%"=="1" (
  echo ==== [skip] tatoeba + opensubtitles -- LAPLACE_SKIP_USAGE=1 ====
) else (
  call :ingest tatoeba || exit /b 1
  call :ingest opensubtitles || exit /b 1
)

rem ---- test-data annex (document; image/audio are stubs) -----------------------
if exist "!INGEST!\test-data\text" (
  call :ingest document "!INGEST!\test-data\text" || exit /b 1
) else (
  echo ==== [skip] test-data text annex not found: !INGEST!\test-data\text ====
)

rem ---- local repos (manifest functionality.repositories) -----------------------
for %%r in (Laplace X_BONEYARD llama-workspace SpecEditor TournamentManager Laplace-Space-Game temp-llama-models) do (
  if exist "!REPOS!\%%r" (
    call :ingest repo "!REPOS!\%%r" || exit /b 1
  ) else (
    echo ==== [skip] repo not found: !REPOS!\%%r ====
  )
)

rem ---- authority sources (manifest functionality.authority_sources) ------------
if exist "!INGEST!\code-authority" (
  for /d %%a in ("!INGEST!\code-authority\*") do (
    call :ingest repo "%%a" || exit /b 1
  )
) else (
  echo ==== [skip] authority sources -- run scripts\win\download-code-data.cmd authority ====
)

rem ---- models (manifest order: BEFORE deferred lexical) ------------------------
if "%LAPLACE_SKIP_MODELS%"=="1" (
  echo ==== [skip] safetensor snapshots -- LAPLACE_SKIP_MODELS=1 ====
  goto stage_lexical
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

:stage_lexical
rem ---- deferred lexical bulk ---------------------------------------------------
if "%LAPLACE_SKIP_LEXICAL_BULK%"=="1" (
  echo ==== [skip] deferred lexical bulk -- LAPLACE_SKIP_LEXICAL_BULK=1 ====
) else if "%LAPLACE_LADDER_DRY%"=="1" (
  echo ==== [dry] seed-deferred-lexical.cmd ====
) else (
  call "%~dp0seed-deferred-lexical.cmd" || exit /b 1
)

cd /d "%LAPLACE_ROOT%"
echo ==== LADDER COMPLETE ====
exit /b 0

rem ============================================================================
rem Subroutines
rem ============================================================================

:ingest
if "%LAPLACE_LADDER_DRY%"=="1" (
  echo ==== [dry] ingest %* ====
  exit /b 0
)
echo ==== ingest %* ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest %*
if errorlevel 1 exit /b 1
exit /b 0

rem Resolve model snapshot: %1=primary env, %2=legacy env, %3=hub family dir, %4=output var name
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
