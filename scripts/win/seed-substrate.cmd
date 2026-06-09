@echo off
setlocal
rem ==== Invention-complete witness seed (manifest-driven) ===================
rem Drop/recreate laplace, run full lexical ladder + all repos + test-data +
rem discovered hub models. See witness-manifest.json for ordering law.
rem cwd on D: so /vault junctions resolve to D:\Data\Ingest.
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "PGPASSWORD=postgres"
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace"
set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ROOT%\build-win\core\perfcache\laplace_t0_perfcache.bin"
set "INGEST=D:\Data\Ingest"
set "REPOS=D:\Repositories"
set "MODELS=D:\Models\hub"
rem Witness language scope: all multilingual sources honor this (per-source override: LAPLACE_TATOEBA_LANGS etc.)
rem English-only seed: en resolves eng/en-US via LanguageReference. Cross-lang edges off unless LAPLACE_EMIT_CROSS_LANG=1
set "LAPLACE_INGEST_LANGS=en"
set "LAPLACE_INGEST_WORKERS=1"
set "LAPLACE_DECOMPOSE_WORKERS=1"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"

echo ==== DROP + recreate laplace ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();" -c "DROP DATABASE IF EXISTS laplace;" || exit /b 1
"%PGBIN%\createdb.exe" -h localhost -U postgres laplace || exit /b 1
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || exit /b 1

echo ==== post-create identity health ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -v ON_ERROR_STOP=1 -c "SET search_path = laplace, public; SELECT * FROM substrate_health();" || exit /b 1

cd app
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1

rem ---- Phase 8a: lexical ladder (witness-manifest.json order) --------------
rem Floor: unicode (L0) + iso639 (L1). WordNet (L2) is the foundational lexicon — first.
rem Then foundational L2 semantics (verbnet/propbank/atomic/conceptnet/ud), usage giants last, L3 bind.
for %%s in (unicode iso639 wordnet verbnet propbank atomic2020 conceptnet ud tatoeba opensubtitles wiktionary omw framenet semlink) do (
  echo ==== ingest %%s ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest %%s || exit /b 1
)

rem ---- Phase 8b: all iteration-stack repos ----------------------------------
for %%r in (Laplace X_BONEYARD llama-workspace SpecEditor TournamentManager Laplace-Space-Game temp-llama-models) do (
  if exist "%REPOS%\%%r" (
    echo ==== ingest repo %%r ====
    dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest repo "%REPOS%\%%r" || exit /b 1
  ) else (
    echo ==== [skip] repo not found: %REPOS%\%%r ====
  )
)

rem ---- Phase 8c: test-data documents + modality annex -----------------------
set "BOOKS=%INGEST%\test-data\text"
for %%f in ("%BOOKS%\*.txt") do (
  echo ==== db-roundtrip %%~nxf ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- db-roundtrip "%%~f" || exit /b 1
)
if exist "%INGEST%\test-data\text\code.py" (
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- db-roundtrip "%INGEST%\test-data\text\code.py" || exit /b 1
)
if exist "%INGEST%\test-data\text\data.json" (
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- db-roundtrip "%INGEST%\test-data\text\data.json" || exit /b 1
)
echo ==== ingest image (test-data annex) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest image "%INGEST%\test-data\images" || exit /b 1
echo ==== ingest audio (test-data annex) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest audio "%INGEST%\test-data\audio" || exit /b 1

rem ---- Phase 8e: code corpora (download-first if absent) --------------------
if exist "%INGEST%\tiny-codes" (
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest tiny-codes "%INGEST%\tiny-codes" || exit /b 1
) else (
  echo ==== [skip] tiny-codes — run scripts\win\download-code-data.cmd tiny-codes ====
)
if exist "%INGEST%\stack-v2" (
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest stack "%INGEST%\stack-v2" || exit /b 1
) else (
  echo ==== [skip] stack-v2 — run scripts\win\download-code-data.cmd stack-v2 ====
)

rem ---- Phase 8d: discover all hub model snapshots --------------------------
for /d %%m in ("%MODELS%\models--*") do (
  for /d %%s in ("%%m\snapshots\*") do (
    if exist "%%s\tokenizer.json" if exist "%%s\config.json" (
      echo ==== ingest model %%s ====
      dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest model "%%s" || exit /b 1
    )
  )
)

cd /d "%LAPLACE_ROOT%"
echo ==== substrate audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1

echo ==== SEED-SUBSTRATE COMPLETE ====
