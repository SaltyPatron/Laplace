@echo off
setlocal EnableDelayedExpansion
rem ============================================================================
rem  Laplace master end-to-end (Windows)
rem ============================================================================
rem  Single orchestration script: clean -> codegen -> build -> perfcache -> DB ->
rem  full witness ladder ingest -> model deposition -> verify.
rem
rem  Agent build/deploy rules: .github\instructions\build-environment.instructions.md
rem  (extensions deploy to D:\Data\Postgres\laplace via install-extensions.cmd — NOT Program Files)
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
rem shift /1 keeps %0 intact — plain shift rotates %0 away and breaks %~dp0
if /i "%~1"=="--skip-clean"  ( set "SKIP_CLEAN=1"  & shift /1 & goto parse_args )
if /i "%~1"=="--skip-models" ( set "SKIP_MODELS=1" & shift /1 & goto parse_args )
if /i "%~1"=="--db-only"    ( set "DB_ONLY=1"     & shift /1 & goto parse_args )
echo unknown flag: %~1
exit /b 2

:args_done
if "%SKIP_MODELS%"=="1" set "LAPLACE_SKIP_MODELS=1"

call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

rem Connection/data-path constants come from env.cmd (single source; pre-set to override).

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
  rem never delete a tree mid-build: take its mutex first (waits for live builds)
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win || exit /b 1
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win-ext || exit /b 1
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
rem Phase 8 — Witness ladder (single source: seed-ladder.cmd ⇔ witness-manifest.json)
rem --------------------------------------------------------------------------
echo.
echo ===== PHASE 8 — WITNESS LADDER =====
rem Ladder covers floor → *Net hub → proof path → models → deferred lexical.
rem e2e-master REQUIRES models unless --skip-models (ladder errors on a missing snapshot).
call "%~dp0seed-ladder.cmd" || exit /b 1

rem ---- db-roundtrip proofs over the document annex ---------------------------
cd "%LAPLACE_ROOT%\app"
if exist "!INGEST!\test-data\text" (
  for %%f in ("!INGEST!\test-data\text\*.txt") do (
    echo ==== db-roundtrip proof %%~nxf ====
    dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- db-roundtrip "%%~f" || exit /b 1
  )
)

rem --------------------------------------------------------------------------
rem Phase 9 — Verify
rem --------------------------------------------------------------------------
:phase_verify
echo.
echo ===== PHASE 9 — VERIFY =====
cd /d "%LAPLACE_ROOT%"

echo ==== engine gtest (excl. regress label) ====
call "%~dp0test-engine.cmd" -LE regress -j1 || exit /b 1

echo ==== pg_regress ====
call "%~dp0regress.cmd" || exit /b 1

echo ==== substrate audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1

echo ==== generation content index ====
call "%~dp0index-content.cmd" laplace deep || exit /b 1

echo ==== smoke: substrate_counts + consensus_stats ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -c "SELECT * FROM laplace.substrate_counts();" -c "SELECT * FROM laplace.consensus_stats();" -c "SELECT pg_size_pretty(pg_database_size('laplace')) AS db_size;" || exit /b 1

echo.
echo ===== E2E-MASTER COMPLETE =====
exit /b 0
