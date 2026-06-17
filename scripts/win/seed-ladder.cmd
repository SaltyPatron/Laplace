@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  seed-ladder.cmd -- THE witness ladder (executable mirror of witness-manifest.json)
rem ============================================================================
rem  Single source of truth for ingest ordering. Callers: e2e-master.cmd,
rem  seed-substrate.cmd, seed-resume-prove.cmd. Do NOT copy this ladder into a
rem  new orchestrator -- call it (lesson 2026-06-10: three divergent copies).
rem
rem  Cadence = a SIGNAL-DEPENDENCY STACK (not a skip list); each layer presupposes the prior:
rem    floor      -- unicode (codepoint atoms) + iso639 (language axis): the only dedup floor
rem    document   -- books, RIGHT AFTER the floor: raw-text distributional trajectories that
rem                  prove AI-via-SQL answers from text alone; the layers below ENRICH, not core
rem    knowledge  -- UNIFORM :ingest steps (no source special-cased), wordnet first (the rest
rem                  bind to its ILI-anchored synsets/senses): wordnet omw verbnet propbank
rem                  framenet semlink conceptnet atomic2020 ud wiktionary
rem    usage      -- tatoeba + opensubtitles: language in use
rem    code       -- CAPSTONE right before models: stack + Laplace + code-authority/* + tiny-codes
rem    models     -- LAST: deposition presupposes export, which the code capstone proves
rem
rem  Anchor law: concepts are decomposed CONTENT (synset = its ILI codepoints, NOT the offset);
rem  witness -> source_id, category/pos -> IS_A/HAS_POS. ConceptAnchor + CategoryAnchor are the
rem  shared de-blob surfaces; decomposers are thin callers, never minting OfCanonical blobs.
rem
rem  Contract (caller responsibilities):
rem    - env.cmd already called; target DB exists with extensions installed
rem    - Laplace.Cli already built Release (ladder uses dotnet run --no-build)
rem
rem  Knobs (all optional; value-checked, not defined-checked):
rem    LAPLACE_LADDER_START        floor (default) | proof  -- proof skips floor+nets (resume)
rem    LAPLACE_LADDER_DRY          1 = print the resolved plan, ingest nothing
rem    LAPLACE_LADDER_STOP         nets = stop after the knowledge layer; usage = stop after usage
rem    LAPLACE_SKIP_USAGE          1 (default) skips tatoeba/opensubtitles
rem    LAPLACE_SKIP_MODELS         1 skips safetensor deposits; UNSET/0 = models REQUIRED (error if missing)
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
if not defined LAPLACE_LADDER_START set "LAPLACE_LADDER_START=floor"
rem LAPLACE_LADDER_STOP=nets ends the run after the *Net cluster (floor + lexical/semantic only) —
rem the targeted scope for iterating on the lexical/semantic decomposers. Unset = the full cadence
rem (… → usage → tiny-codes → books → repo capstone → models). NEVER a substitute for the gate.

cd /d "%LAPLACE_ROOT%\app"

if /i "%LAPLACE_LADDER_START%"=="proof" (
  echo ==== ladder start: proof path - floor + *Net cluster assumed present ====
  goto stage_proof
)

rem ---- floor: L0-L1 ----------------------------------------------------------
call :ingest unicode || exit /b 1
call :ingest iso639 || exit /b 1

rem ---- books / long text RIGHT AFTER the floor: the corpus that proves AI-via-SQL answers from
rem ---- raw text alone (PRECEDES/FOLLOWS/CO_OCCURS distributional trajectories -- attention needs
rem ---- nothing more to start answering). The structured layers below ENRICH; they are not the core.
if exist "!INGEST!\test-data\text" (
  call :ingest document "!INGEST!\test-data\text" || exit /b 1
) else (
  echo ==== [skip] test-data text annex not found: !INGEST!\test-data\text ====
)

rem ---- structured knowledge decomposers -- uniform :ingest steps, LAPLACE_INGEST_LANGS scopes ----
rem ---- (wordnet first: omw/verbnet/propbank/framenet/semlink/conceptnet bind to its synsets/senses)
for %%s in (wordnet omw verbnet propbank framenet semlink conceptnet atomic2020 ud wiktionary) do (
  call :ingest %%s || exit /b 1
)
if /i "%LAPLACE_LADDER_STOP%"=="nets" goto ladder_done

:stage_proof
rem ---- world usage: Tatoeba sentences + OpenSubtitles dialogue (language in use) ----
if "%LAPLACE_SKIP_USAGE%"=="1" (
  echo ==== [skip] tatoeba + opensubtitles -- LAPLACE_SKIP_USAGE=1 ====
) else (
  call :ingest tatoeba || exit /b 1
  call :ingest opensubtitles || exit /b 1
)
if /i "%LAPLACE_LADDER_STOP%"=="usage" goto ladder_done

rem ---- CODE CAPSTONE (boil the ocean), RIGHT BEFORE models -- code through the full tree-sitter/
rem ---- grammar/AST pipeline; success = the whole machinery works end to end (the precondition for
rem ---- model export). All the code info together: stack-v2 + Laplace + code-authority/* + tiny-codes
rem ---- (code reasoning snippets). X_BONEYARD/incidental repos are not the default capstone. ----
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

rem ---- models LAST: model ingestion presupposes export, which the code capstone above proves ----
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

rem ============================================================================
rem Subroutines
rem ============================================================================

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
