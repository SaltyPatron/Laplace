@echo off
setlocal EnableDelayedExpansion
rem Resume the proof path on an existing DB (no drop): tiny-codes/stack -> annex ->
rem repos -> authority -> audit. Requires floor + *Net cluster (L0-L3) already present.
rem Delegates to seed-ladder.cmd with LAPLACE_LADDER_START=proof; lexical bulk and
rem models are skipped by default here (override with LAPLACE_SKIP_*=0).
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
rem Connection/data-path constants come from env.cmd. Reproducibility pins below are deliberate.
set "LAPLACE_INGEST_LANGS=en"
set "LAPLACE_INGEST_WORKERS=4"
set "LAPLACE_DECOMPOSE_WORKERS=1"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_SKIP_MODELS set "LAPLACE_SKIP_MODELS=1"
if not defined LAPLACE_SKIP_LEXICAL_BULK set "LAPLACE_SKIP_LEXICAL_BULK=1"
set "LAPLACE_LADDER_START=proof"

call "%~dp0build-engine-libs.cmd" || exit /b 1
cd app
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1
cd /d "%LAPLACE_ROOT%"

call "%~dp0seed-ladder.cmd" || exit /b 1

echo ==== substrate audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1
echo ==== SEED-RESUME-PROVE COMPLETE ====
