@echo off
setlocal EnableDelayedExpansion
rem Resume proof path: tiny-codes/stack → test-data annex → repos → audit.
rem Requires *Net cluster through semlink (L0–L3) already in DB. Does not drop laplace.
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "PGPASSWORD=postgres"
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace"
set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ROOT%\build-win\core\perfcache\laplace_t0_perfcache.bin"
set "INGEST=D:\Data\Ingest"
set "REPOS=D:\Repositories"
set "LAPLACE_INGEST_LANGS=en"
if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"
set "LAPLACE_INGEST_WORKERS=4"
set "LAPLACE_DECOMPOSE_WORKERS=1"
set "LAPLACE_COPY_VALIDATE=1"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=1"

call "%~dp0build-engine-libs.cmd" || exit /b 1
cd app
dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q || exit /b 1

if exist "!INGEST!\tiny-codes" (
  echo ==== ingest tiny-codes ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest tiny-codes "!INGEST!\tiny-codes" || exit /b 1
)
if exist "!INGEST!\stack-v2" (
  echo ==== ingest stack-v2 ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest stack "!INGEST!\stack-v2" || exit /b 1
)
rem ---- test-data annex — before repos ----------------------------------------
if exist "!INGEST!\test-data\text" (
  echo ==== ingest document - test-data text annex ====
  dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest document "!INGEST!\test-data\text" || exit /b 1
)
echo ==== [skip] image — ImageDecomposer is a stub (no file ingest yet) ====
echo ==== [skip] audio — AudioDecomposer is a stub (no file ingest yet) ====

rem ---- repos (after test-data annex) ----------------------------------------
for %%r in (Laplace X_BONEYARD llama-workspace SpecEditor TournamentManager Laplace-Space-Game temp-llama-models) do (
  if exist "!REPOS!\%%r" (
    echo ==== ingest repo %%r ====
    dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest repo "!REPOS!\%%r" || exit /b 1
  )
)

rem ---- authority sources (manifest functionality.authority_sources) ----------
if exist "!INGEST!\code-authority" (
  for /d %%a in ("!INGEST!\code-authority\*") do (
    echo ==== ingest repo %%~nxa ====
    dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- ingest repo "%%a" || exit /b 1
  )
)
cd /d "%LAPLACE_ROOT%"
echo ==== substrate audit ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d laplace -P pager=off -f "%LAPLACE_ROOT%\scripts\sql\substrate-audit.sql" || exit /b 1
echo ==== SEED-RESUME-PROVE COMPLETE ====
