@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
set "LAPLACE_INGEST_WORKERS=4"
set "LAPLACE_DECOMPOSE_WORKERS=1"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"

call "%~dp0build-engine-libs.cmd" || exit /b 1
cd app
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1

call "%~dp0seed-step.cmd" conceptnet || exit /b 1
call "%~dp0seed-step.cmd" atomic2020 || exit /b 1
call "%~dp0seed-step.cmd" ud || exit /b 1
call "%~dp0seed-step.cmd" wiktionary || exit /b 1
echo ==== SEMANTIC KNOWLEDGE LAYER COMPLETE ====
exit /b 0
