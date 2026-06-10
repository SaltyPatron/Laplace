@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  Laplace master end-to-end (Windows)
rem ============================================================================
rem  Single orchestration script: clean -> codegen -> build -> perfcache -> DB ->
rem  full witness ladder ingest -> model deposition -> verify.
rem
rem  Prerequisites
rem  -------------
rem  - PostgreSQL 18 running on localhost (postgres/postgres)
rem  - Intel oneAPI + VS CMake/Ninja (see env.cmd)
rem  - Junctions so /vault paths resolve (cwd on D:):
rem      D:\vault\Data   -> D:\Data\Ingest
rem      D:\vault\models -> D:\Models\hub
rem  - Unicode UCD 17.0.0 under D:\Data\Ingest\Unicode\Public\17.0.0
rem  - Ladder corpora under D:\Data\Ingest (WordNet, UD, ConceptNet, ...)
rem  - Optional code corpora: scripts\win\download-code-data.cmd
rem
rem  Model weights (required unless --skip-models / LAPLACE_SKIP_MODELS=1)
rem  -----------------------------------------------------------------------
rem  Set snapshot dirs (HF layout: .../snapshots/<rev>/ with config.json,
rem  tokenizer.json, *.safetensors) or rely on auto-discovery under hub:
rem
rem    LAPLACE_MODEL_HUB          default D:\Models\hub
rem    LAPLACE_MODEL_TINYLLAMA    TinyLlama-1.1B-Chat snapshot dir
rem    LAPLACE_MODEL_PHI          microsoft/phi-2 snapshot dir
rem    LAPLACE_MODEL_QWEN25_CODER Qwen2.5-Coder snapshot dir
rem
rem  Legacy aliases also honoured: LAPLACE_TINYLLAMA_DIR, LAPLACE_PHI2_DIR
rem
rem  Usage
rem  -----
rem    scripts\win\e2e-master.cmd
rem    scripts\win\e2e-master.cmd --skip-clean
rem    scripts\win\e2e-master.cmd --skip-models
rem    scripts\win\e2e-master.cmd --db-only
rem
rem  Flags compose: --db-only skips build/ingest/verify (DB bootstrap only).
rem ============================================================================

set "SKIP_CLEAN=0"
set "SKIP_MODELS=0"
set "DB_ONLY=0"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--skip-clean"  ( set "SKIP_CLEAN=1"  & shift & goto parse_args )
if /i "%~1"=="--skip-models" ( set "SKIP_MODELS=1" & shift & goto parse_args )
if /i "%~1"=="--db-only"    ( set "DB_ONLY=1"     & shift & goto parse_args )
echo unknown flag: %~1
exit /b 2

:args_done
if "%SKIP_MODELS%"=="1" set "LAPLACE_SKIP_MODELS=1"

call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "PGPASSWORD=postgres"
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace"
set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ROOT%\build-win\core\perfcache\laplace_t0_perfcache.bin"
set "INGEST=D:\Data\Ingest"
set "REPOS=D:\Repositories"
set "LAPLACE_MODEL_HUB=D:\Models\hub"
if defined LAPLACE_MODEL_HUB_USER set "LAPLACE_MODEL_HUB=%LAPLACE_MODEL_HUB_USER%"

rem Ingest tuning — conservative defaults for reproducible COPY framing proofs.
set "LAPLACE_INGEST_LANGS=en"
if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
if not defined LAPLACE_INGEST_WORKERS set "LAPLACE_INGEST_WORKERS=4"
set "LAPLACE_DECOMPOSE_WORKERS=1"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=1"
if not defined LAPLACE_SKIP_LEXICAL_BULK set "LAPLACE_SKIP_LEXICAL_BULK=0"

rem --------------------------------------------------------------------------
rem Phase 1 — Clean
rem --------------------------------------------------------------------------
if "%SKIP_CLEAN%"=="0" (
  echo.
  echo ===== PHASE 1 — CLEAN =====
  if exist "%LAPLACE_ROOT%\build-win" (
    echo removing build-win ...
    rmdir /s /q "%LAPLACE_ROOT%\build-win"
  )
  if exist "%LAPLACE_ROOT%\build-win-ext" (
    echo removing build-win-ext ...
    rmdir /s /q "%LAPLACE_ROOT%\build-win-ext"
  )
  echo terminating laplace backends + drop database ...
  "%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();" -c "DROP DATABASE IF EXISTS laplace;" || exit /b 1
) else (
  echo.
  echo ===== PHASE 1 — CLEAN [skipped: --skip-clean] =====
)

if "%DB_ONLY%"=="1" goto phase_db_bootstrap

rem --------------------------------------------------------------------------
rem Phase 2 — Codegen (attestation law)
rem --------------------------------------------------------------------------
echo.
echo ===== PHASE 2 — CODEGEN =====
powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\codegen-attestation-law.ps1" || exit /b 1

rem --------------------------------------------------------------------------
rem Phase 3 — Build engine
rem --------------------------------------------------------------------------
echo.
echo ===== PHASE 3 — BUILD ENGINE =====
call "%~dp0build-engine.cmd" || exit /b 1

rem --------------------------------------------------------------------------
rem Phase 4 — Build + install extensions
rem --------------------------------------------------------------------------
echo.
echo ===== PHASE 4 — BUILD EXTENSIONS =====
call "%~dp0build-extensions.cmd" || exit /b 1
call "%~dp0install-extensions.cmd" || exit /b 1

rem --------------------------------------------------------------------------
rem Phase 5 — Build app (dotnet Release)
rem --------------------------------------------------------------------------
echo.
echo ===== PHASE 5 — BUILD APP =====
cd "%LAPLACE_ROOT%\app"
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1
for %%P in (
  Laplace.Engine.Core
  Laplace.Engine.Dynamics
  Laplace.Engine.Synthesis
  Laplace.SubstrateCRUD
  Laplace.Ingestion
  Laplace.Decomposers.Abstractions
  Laplace.Decomposers.Model
  Laplace.Decomposers.Unicode
  Laplace.Decomposers.WordNet
) do (
  if exist "%%P\%%P.csproj" dotnet build "%%P\%%P.csproj" -c Release -v q --nologo || exit /b 1
)
cd /d "%LAPLACE_ROOT%"

rem --------------------------------------------------------------------------
rem Phase 6 — Perf cache (T0 codepoint blob)
rem --------------------------------------------------------------------------
echo.
echo ===== PHASE 6 — PERF CACHE =====
cmake --build build-win --target laplace_t0_perfcache || exit /b 1
if not exist "%LAPLACE_PERFCACHE_BIN%" (
  echo ERROR: perf-cache blob missing at %LAPLACE_PERFCACHE_BIN%
  echo   build-engine should emit laplace_t0_perfcache.bin — check UCD inputs in build-engine.cmd
  exit /b 1
)
for %%F in ("%LAPLACE_PERFCACHE_BIN%") do echo perfcache ready: %%~zF bytes — %%F

rem --------------------------------------------------------------------------
rem Phase 7 — DB bootstrap
rem --------------------------------------------------------------------------
:phase_db_bootstrap
echo.
echo ===== PHASE 7 — DB BOOTSTRAP =====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='laplace'" | findstr 1 >nul
if errorlevel 1 (
  echo creating database laplace ...
  "%PGBIN%\createdb.exe" -h localhost -U postgres laplace || exit /b 1
)
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || exit /b 1
echo post-create identity health:
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -v ON_ERROR_STOP=1 -c "SET search_path = laplace, public; SELECT * FROM substrate_health();" || exit /b 1

if "%DB_ONLY%"=="1" (
  echo.
  echo ===== E2E-MASTER DB-ONLY COMPLETE =====
  exit /b 0
)

rem Ensure CLI is built before ingest
cd "%LAPLACE_ROOT%\app"
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1

rem --------------------------------------------------------------------------
rem Phase 8 — Ingest corpora (witness ladder)
rem --------------------------------------------------------------------------
echo.
echo ===== PHASE 8 — INGEST CORPORA =====

rem ---- L0-L1: floor ---------------------------------------------------------
call :ingest unicode || exit /b 1
call :ingest iso639 || exit /b 1

rem ---- *Net cluster (synset hub law) ----------------------------------------
call :ingest wordnet || exit /b 1
call :ingest omw || exit /b 1
call :ingest verbnet || exit /b 1
call :ingest propbank || exit /b 1
call :ingest framenet || exit /b 1
call :ingest semlink || exit /b 1

rem ---- proof path: code corpora ---------------------------------------------
if exist "!INGEST!\tiny-codes" (
  call :ingest tiny-codes "!INGEST!\tiny-codes" || exit /b 1
) else (
  echo ==== [skip] tiny-codes — run scripts\win\download-code-data.cmd tiny-codes ====
)
if exist "!INGEST!\stack-v2" (
  call :ingest stack "!INGEST!\stack-v2" || exit /b 1
) else (
  echo ==== [skip] stack-v2 — run scripts\win\download-code-data.cmd stack-v2 ====
)

rem ---- world usage (optional) -----------------------------------------------
if not defined LAPLACE_SKIP_USAGE (
  call :ingest tatoeba || exit /b 1
  call :ingest opensubtitles || exit /b 1
) else (
  echo ==== [skip] tatoeba + opensubtitles — LAPLACE_SKIP_USAGE=1 ====
)

rem ---- test-data annex + repos ----------------------------------------------
if exist "!INGEST!\test-data\text" (
  call :ingest document "!INGEST!\test-data\text" || exit /b 1
  for %%f in ("!INGEST!\test-data\text\*.txt") do (
    echo ==== db-roundtrip proof %%~nxf ====
    dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- db-roundtrip "%%~f" || exit /b 1
  )
) else (
  echo ==== [skip] test-data text annex: !INGEST!\test-data\text ====
)

for %%r in (Laplace X_BONEYARD llama-workspace SpecEditor TournamentManager Laplace-Space-Game temp-llama-models) do (
  if exist "!REPOS!\%%r" (
    call :ingest repo "!REPOS!\%%r" || exit /b 1
  ) else (
    echo ==== [skip] repo not found: !REPOS!\%%r ====
  )
)

rem ---- deferred lexical bulk ------------------------------------------------
if "%LAPLACE_SKIP_LEXICAL_BULK%"=="1" (
  echo ==== [skip] deferred lexical bulk — LAPLACE_SKIP_LEXICAL_BULK=1 ====
) else (
  call "%~dp0seed-deferred-lexical.cmd" || exit /b 1
)

rem --------------------------------------------------------------------------
rem Phase 9 — Model ingest
rem --------------------------------------------------------------------------
echo.
echo ===== PHASE 9 — MODEL INGEST =====
if defined LAPLACE_SKIP_MODELS (
  echo ==== [skip] safetensor snapshots — LAPLACE_SKIP_MODELS=1 / --skip-models ====
  goto phase_verify
)

call :resolve_model LAPLACE_MODEL_TINYLLAMA LAPLACE_TINYLLAMA_DIR "models--TinyLlama--TinyLlama-1.1B-Chat-v1.0" TINYLLAMA
if errorlevel 1 exit /b 1
echo ==== model: TinyLlama ====
call :ingest safetensors "!TINYLLAMA!" || exit /b 1

call :resolve_model LAPLACE_MODEL_PHI LAPLACE_PHI2_DIR "models--microsoft--phi-2" PHI
if errorlevel 1 exit /b 1
echo ==== model: Phi-2 ====
call :ingest safetensors "!PHI!" || exit /b 1

call :resolve_model LAPLACE_MODEL_QWEN25_CODER LAPLACE_QWEN25_CODER_DIR "models--Qwen--Qwen2.5-Coder-3B-Instruct" QWEN
if errorlevel 1 exit /b 1
echo ==== model: Qwen2.5-Coder ====
call :ingest safetensors "!QWEN!" || exit /b 1

rem --------------------------------------------------------------------------
rem Phase 10 — Verify
rem --------------------------------------------------------------------------
:phase_verify
echo.
echo ===== PHASE 10 — VERIFY =====
cd /d "%LAPLACE_ROOT%"

echo ==== engine gtest (excl. regress label) ====
call "%~dp0test-engine.cmd" -LE regress -j1 || exit /b 1

echo ==== pg_regress ====
call "%~dp0regress.cmd" || exit /b 1

echo ==== substrate audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1

echo ==== smoke: substrate_counts + consensus_stats ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -c "SELECT * FROM laplace.substrate_counts();" -c "SELECT * FROM laplace.consensus_stats();" -c "SELECT pg_size_pretty(pg_database_size('laplace')) AS db_size;" || exit /b 1

echo.
echo ===== E2E-MASTER COMPLETE =====
exit /b 0

rem ============================================================================
rem Subroutines
rem ============================================================================

:ingest
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
  echo ERROR: model not found — set %~1 to a snapshot dir, or download into:
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
