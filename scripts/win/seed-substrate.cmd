@echo off
setlocal EnableDelayedExpansion
rem ==== Invention-complete witness seed (manifest-driven) =====================
rem Build (incremental) -> DROP+recreate laplace -> seed-ladder.cmd -> audit.
rem The ladder itself (ordering law, synset hub law, models, deferred lexical)
rem lives in seed-ladder.cmd -- the executable mirror of witness-manifest.json.
rem Defaults here: LAPLACE_SKIP_MODELS=1, LAPLACE_SKIP_USAGE=0 (full witness; set =1 to skip usage).
rem cwd on D: so /vault junctions resolve to D:\Data\Ingest.
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
rem Connection/data-path constants come from env.cmd. Reproducibility pins below are deliberate.
set "LAPLACE_INGEST_LANGS=en"
if not defined LAPLACE_INGEST_WORKERS set "LAPLACE_INGEST_WORKERS=4"
set "LAPLACE_DECOMPOSE_WORKERS=1"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_SKIP_MODELS set "LAPLACE_SKIP_MODELS=1"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=0"

echo ==== build engine + extensions (native exports must match CLI) ====
call "%~dp0build-engine-libs.cmd" || exit /b 1
call "%~dp0build-extensions.cmd" || exit /b 1
call "%~dp0install-extensions.cmd" || exit /b 1

echo ==== DROP + recreate laplace ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();" -c "DROP DATABASE IF EXISTS laplace;" || exit /b 1
"%PGBIN%\createdb.exe" -h localhost -U postgres laplace || exit /b 1
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS postgis;" -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;" || exit /b 1

echo ==== post-create identity health ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -v ON_ERROR_STOP=1 -c "SET search_path = laplace, public; SELECT * FROM substrate_health();" || exit /b 1

cd app
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1
cd /d "%LAPLACE_ROOT%"

call "%~dp0seed-ladder.cmd" || exit /b 1

echo ==== substrate audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1

echo ==== SEED-SUBSTRATE COMPLETE ====
