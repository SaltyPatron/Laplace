@echo off
setlocal
rem Deferred heavy lexical: conceptnet → atomic2020 → ud → wiktionary.
rem Run after proof path (seed-resume-prove or full seed functionality block).
rem Idempotent: completed sources short-circuit on layer-complete marker.
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
rem Connection/data-path constants come from env.cmd. Reproducibility pins below are deliberate.
set "LAPLACE_INGEST_LANGS=en"
if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
set "LAPLACE_INGEST_WORKERS=4"
set "LAPLACE_DECOMPOSE_WORKERS=1"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"

call "%~dp0build-engine-libs.cmd" || exit /b 1
cd app
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1

echo ==== ingest conceptnet (/c/en/ graph) ====
rem Serial commit is decomposer-owned now (RelationTripleDecomposerBase: StrictSerial);
rem only the memory pin remains.
set "LAPLACE_INGEST_COMMIT_ROWS=50000"
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest conceptnet || exit /b 1
set "LAPLACE_INGEST_COMMIT_ROWS="
echo ==== ingest atomic2020 ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest atomic2020 || exit /b 1
echo ==== ingest ud (en_* treebanks only) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest ud --langs en || exit /b 1
echo ==== ingest wiktionary (English jsonl) ====
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest wiktionary --langs en || exit /b 1
echo ==== DEFERRED-LEXICAL COMPLETE ====
