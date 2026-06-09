@echo off
setlocal
rem ==== Laplace full from-scratch E2E (Windows) ============================
rem Drops the laplace DB, recreates it + extensions, seeds the whole T0..Tn
rem ladder, deposits the TinyLlama model, then ingests the test-data book
rem library. Everything the parameterized e2e.cmd leaves out.
rem cwd stays on D: so the hardcoded /vault junctions resolve.
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "PGPASSWORD=postgres"
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace"
set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ROOT%\build-win\core\perfcache\laplace_t0_perfcache.bin"
set "BOOKS=D:\Data\Ingest\test-data\text"
set "MODEL=D:\Models\hub\models--TinyLlama--TinyLlama-1.1B-Chat-v1.0\snapshots\fe8a4ea1ffedaf415f4da2f062534de366a451e6"
set "MODEL_CODER=D:\Models\hub\models--Qwen--Qwen2.5-Coder-7B-Instruct\snapshots\c03e6d358207e414f1eca0bb1891e29f1db0e242"
set "TINY_CODES=D:\Data\Ingest\tiny-codes"
set "STACK_V2=D:\Data\Ingest\stack-v2"
rem Phased sources (Unicode aliases, model ETL) tag CommitEpoch so parallel
rem commits stay within-epoch; cross-epoch barriers preserve referential order.
rem Start at 1 when debugging; raise after measured tuning on your box.
if not defined LAPLACE_INGEST_WORKERS set "LAPLACE_INGEST_WORKERS=2"
rem LAPLACE_DECOMPOSE_WORKERS pinned to 1 for deterministic, reproducible
rem batching while we chase the entities binary-COPY framing fault. Parallel
rem decode is NOT the root cause (the 22P04 "row field count is 0" still fired
rem with both workers=1 on a resumed DB; it is a real serializer/COPY framing
rem fault and/or DB-pollution from earlier aborted runs). Always run this
rem script from a CLEAN drop (it drops+recreates below) so referential proofs
rem are not confounded by half-committed rows from a previous failed run.
set "LAPLACE_DECOMPOSE_WORKERS=1"
rem Validate the native PG-binary COPY blob row-framing right before each COPY
rem so any corruption is caught at its FIRST occurrence with exact byte offset
rem + hex window instead of surfacing later as an opaque 22P04 from Postgres.
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"

echo ==== DROP + recreate laplace (from scratch) ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();" -c "DROP DATABASE IF EXISTS laplace;" || exit /b 1
"%PGBIN%\createdb.exe" -h localhost -U postgres laplace || exit /b 1
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || exit /b 1

cd app
echo ==== build CLI ====
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1

rem ---- lexical core (L0–L2 semantics) --------------------------------------
for %%s in (unicode iso639 wordnet verbnet propbank atomic2020 conceptnet ud) do (
  echo ==== seed %%s ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest %%s || exit /b 1
)

rem ---- functionality: repos + code corpora (before world usage) ------------
echo ==== code: Laplace repo ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest repo "%LAPLACE_ROOT%" || exit /b 1

rem Download: scripts\win\download-code-data.cmd tiny-codes  (requires HF_TOKEN)
if exist "%TINY_CODES%" (
  echo ==== code: tiny-codes ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest tiny-codes "%TINY_CODES%" || exit /b 1
) else (
  echo ==== [skip] tiny-codes not found at %TINY_CODES% ====
)

rem Download: scripts\win\download-code-data.cmd stack-v2  (HF_TOKEN + gated terms)
if exist "%STACK_V2%" (
  echo ==== code: stack-v2 ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest stack "%STACK_V2%" || exit /b 1
) else (
  echo ==== [skip] stack-v2 not found at %STACK_V2% ====
)

for %%f in ("%BOOKS%\*.txt") do (
  echo ==== book %%~nxf ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- db-roundtrip "%%~f" || exit /b 1
)

rem ---- world usage (tatoeba / opensubtitles / wiktionary) -------------------
for %%s in (tatoeba opensubtitles wiktionary) do (
  echo ==== seed %%s ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest %%s || exit /b 1
)

rem ---- L3 bind --------------------------------------------------------------
for %%s in (omw framenet semlink) do (
  echo ==== seed %%s ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest %%s || exit /b 1
)

if not defined LAPLACE_SKIP_MODELS (
  echo ==== model: TinyLlama-1.1B ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest safetensors "%MODEL%" || exit /b 1

  echo ==== model: Qwen2.5-Coder-7B-Instruct ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest safetensors "%MODEL_CODER%" || exit /b 1
) else (
  echo ==== [skip] safetensor snapshots — LAPLACE_SKIP_MODELS=1 ====
)

echo ==== audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -c "SELECT * FROM laplace.substrate_counts();" -c "SELECT * FROM laplace.consensus_stats();" -c "SELECT pg_size_pretty(pg_database_size('laplace')) AS db_size;"

echo ==== E2E-FULL COMPLETE ====
