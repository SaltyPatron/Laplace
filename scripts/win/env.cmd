@echo off
if defined LAPLACE_ENV_LOADED exit /b 0
rem The installed declaration is shared with PowerShell and MSBuild. No shell evaluation.
set "LAPLACE_MACHINE_ENV_IMPORTED="
for /f "usebackq delims=" %%L in (`pwsh -NoProfile -File "%~dp0machine-env.ps1" -ForCmd`) do set "%%L"
if not defined LAPLACE_MACHINE_ENV_IMPORTED exit /b 1
set "NoDefaultCurrentDirectoryInExePath="
set "PSModulePath="
rem Toolchain selection belongs to the developer shell, not path resolution.
set "Platform="
setlocal EnableDelayedExpansion
set "_LIB="
for %%D in ("!LIB:;=" "!") do (if not "%%~D"=="" if exist "%%~D\" set "_LIB=!_LIB!%%~D;")
endlocal & set "LIB=%_LIB%"
for %%I in ("%~dp0..\..") do set "LAPLACE_ROOT=%%~fI"
rem Installation roots come from machine.env; derived paths do not choose a drive.

if not defined LAPLACE_BUILD_ROOT if defined LAPLACE_BUILD set "LAPLACE_BUILD_ROOT=%LAPLACE_BUILD%\Laplace"

if not defined LAPLACE_DEPLOY set "LAPLACE_DEPLOY=%LAPLACE_DATA_ROOT%\deploy"
rem geos/proj/gdal (and sqlite for PROJ) from external/ — scripts\win\build-deps.cmd
if not defined LAPLACE_DEPS_PREFIX set "LAPLACE_DEPS_PREFIX=%LAPLACE_DEPS%"
if not defined LAPLACE_DEPS_BUILD set "LAPLACE_DEPS_BUILD=%LAPLACE_BUILD_ROOT%\build-deps"
if not defined LAPLACE_ENGINE_BUILD set "LAPLACE_ENGINE_BUILD=%LAPLACE_BUILD_ROOT%\build-win"
if not defined LAPLACE_EXT_BUILD set "LAPLACE_EXT_BUILD=%LAPLACE_BUILD_ROOT%\build-win-ext"
if not defined LAPLACE_ENGINE_BUILD_ASAN set "LAPLACE_ENGINE_BUILD_ASAN=%LAPLACE_BUILD_ROOT%\build-win-asan"
rem PostgreSQL built from external/postgresql (scripts\win\build-pg.cmd), mirroring the
rem Linux ${LAPLACE_DEPS_PREFIX}/pgsql-18 install prefix from external/CMakeLists.txt.
if not defined LAPLACE_PG_BUILD set "LAPLACE_PG_BUILD=%LAPLACE_BUILD_ROOT%\build-pg"
if not defined LAPLACE_PG_PREFIX if defined LAPLACE_PG_DIR set "LAPLACE_PG_PREFIX=%LAPLACE_PG_DIR%"
if not defined LAPLACE_PG_PREFIX set "LAPLACE_PG_PREFIX=%LAPLACE_DEPS_PREFIX%\pgsql-18"
if not defined LAPLACE_TOOLS set "LAPLACE_TOOLS=%LAPLACE_DATA_ROOT%\tools"
rem MSYS2 (make, sh, curl, mingw-w64-ucrt gcc): the one Laplace-Operations declares (LAPLACE_MSYS2); ensure-stockfish-toolchain.ps1
rem builds Stockfish with it, and installs a copy under LAPLACE_TOOLS only when none is declared.
if not defined MSYS2_ROOT if defined LAPLACE_MSYS2 set "MSYS2_ROOT=%LAPLACE_MSYS2%"
if not defined MSYS2_ROOT set "MSYS2_ROOT=%LAPLACE_TOOLS%\msys64"
if not defined LAPLACE_CUTECHESS_BUILD set "LAPLACE_CUTECHESS_BUILD=%LAPLACE_BUILD_ROOT%\build-cutechess"
if not defined LAPLACE_OUT set "LAPLACE_OUT=%LAPLACE_BUILD_ROOT%\out"
if not defined LAPLACE_PUBLISH_ENDPOINT set "LAPLACE_PUBLISH_ENDPOINT=%LAPLACE_OUT%\endpoint"
if not defined LAPLACE_PUBLISH_MIGRATIONS set "LAPLACE_PUBLISH_MIGRATIONS=%LAPLACE_OUT%\migrations"
if not defined LAPLACE_IIS_API set "LAPLACE_IIS_API=%LAPLACE_DATA_ROOT%\iis\laplace-api"
rem Ingest / CLI binary is the ReadyToRun publish tree. Plain `dotnet build` output
rem under net10.0 is for IDE/build only — runtime clrjit 0xc0000409 killed ingest on
rem .NET 10.0.9; do not point LAPLACE_CLI_EXE at the non-R2R build. seed-step
rem :ensure_cli publishes this tree when missing.
if not defined LAPLACE_CLI_EXE set "LAPLACE_CLI_EXE=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Cli\Release\net10.0-r2r\Laplace.Cli.exe"
if not defined LAPLACE_CLI_DLL set "LAPLACE_CLI_DLL=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Cli\Release\net10.0-r2r\Laplace.Cli.dll"
set "LAPLACE_DEPLOY_PG=%LAPLACE_DEPLOY:\=/%"
rem The server's binaries are the prefix's (LAPLACE_PG_PREFIX), not an installer's; override PGBIN for another server.
if not defined PGBIN set "PGBIN=%LAPLACE_PG_PREFIX%\bin"
rem The external source tree the engine, the extensions and the app read (blake3, eigen, spectra, fathom, tree-sitter,
rem postgis, tree-sitter-grammars), under the data root; on HART-DESKTOP each entry is a junction to the checkout the
rem Laplace-Operations manifest keeps under D:\Libraries\src (and the grammars under D:\Data\Ingest\TreeSitter).
if not defined LAPLACE_EXTERNAL set "LAPLACE_EXTERNAL=%LAPLACE_DATA_ROOT%\external"
rem Versioned dependency prefixes and runtime DLL paths come from the installed manifest.
if not defined MKLROOT if defined LAPLACE_ONEAPI set "MKLROOT=%LAPLACE_ONEAPI%\mkl\latest"
rem The DLLs the engine's core loads at run time (the app's OpenAPI emission and the perfcache tools load it too).
set "PATH=%PGBIN%;%PATH%"
set "PATH=%LAPLACE_ENGINE_BUILD%\core;%LAPLACE_ENGINE_BUILD%\dynamics;%LAPLACE_ENGINE_BUILD%\synthesis;%PATH%"
rem Runtime DLLs for laplace_geom (geos_c / proj / sqlite) — must precede system PATH.

if not defined LAPLACE_DBNAME set "LAPLACE_DBNAME=laplace"
if not defined LAPLACE_CANONICAL_DB set "LAPLACE_CANONICAL_DB=%LAPLACE_DBNAME%"
if not defined LAPLACE_ISOLATE_PREFIX set "LAPLACE_ISOLATE_PREFIX=laplace_d"
rem LAPLACE_PGHOST/LAPLACE_PGUSER feed BOTH the CLI connection string below AND
rem every scripted psql verify/health check, so the writer and the verifier can
rem never target different databases. Remote seeding (e.g. Windows -> hart-server):
rem set LAPLACE_PGHOST=hart-server before calling any seed script.
if not defined LAPLACE_PGHOST set "LAPLACE_PGHOST=localhost"
if not defined LAPLACE_PGPORT set "LAPLACE_PGPORT=5432"
if not defined LAPLACE_PGUSER if defined LAPLACE_ROLE set "LAPLACE_PGUSER=%LAPLACE_ROLE%"
if not defined LAPLACE_PGUSER set "LAPLACE_PGUSER=laplace"
if not defined LAPLACE_PG_SERVICE_ACCOUNT set "LAPLACE_PG_SERVICE_ACCOUNT=NT AUTHORITY\NetworkService"
rem Passwords come from the credential provider/pgpass or explicit process configuration.
if not defined LAPLACE_DB set "LAPLACE_DB=Host=%LAPLACE_PGHOST%;Port=%LAPLACE_PGPORT%;Username=%LAPLACE_PGUSER%;Database=%LAPLACE_DBNAME%;Command Timeout=0"
if not defined LAPLACE_BILLING_BYPASS set "LAPLACE_BILLING_BYPASS=true"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=0"
if not defined LAPLACE_SKIP_MODELS set "LAPLACE_SKIP_MODELS=0"




rem Ingest batch/commit/worker counts are derived at runtime from Intel topology
rem (CpuTopology) + RAM (IngestSizing.ResolveForSource). Do not set LAPLACE_INGEST_* here.

if not defined LAPLACE_TBB_MAX_THREADS_PER_CORE set "LAPLACE_TBB_MAX_THREADS_PER_CORE=1"
rem MKL/TBB/native thread counts are reconciled from Intel P-core topology at CLI startup
rem (NativeRuntimeEnv.ApplyFromTopology). Values here are fallbacks for non-CLI tools only.
if not defined MKL_DYNAMIC set "MKL_DYNAMIC=0"
REM Server GC for the ingest CLI: measured live (Lumbras chess, 7 compose workers)
REM at 1.9GB/s allocation rate, 73 gen0 + 29 gen1 collections/s, 16% of wall time
REM in GC pause under the default workstation GC — every collection suspends all
REM pinned workers at once. Server GC = per-core heaps + parallel collection.
REM Heap count capped to the P-core budget so 32 logical procs don't inflate RSS.
if not defined DOTNET_gcServer set "DOTNET_gcServer=1"
rem Build/test parallelism: Ninja defaults to logical CPU count (32 on i9-14900KS).
rem icx + Eigen under -j32 AV's in Live Interval Analysis (gram_schmidt ICE). Cap to the
rem same P-core budget as MKL/TBB/GCHeapCount. Override CMAKE_BUILD_PARALLEL_LEVEL to raise.
rem Set LAPLACE_TEST_SERIAL=1 to force serial ctest/regress/dotnet-test orchestration.
if not defined CTEST_PARALLEL_LEVEL (
  if defined LAPLACE_TEST_SERIAL (
    set "CTEST_PARALLEL_LEVEL=1"
  ) else (
    if defined CMAKE_BUILD_PARALLEL_LEVEL set "CTEST_PARALLEL_LEVEL=%CMAKE_BUILD_PARALLEL_LEVEL%"
  )
)
if not defined LAPLACE_PERFCACHE_BIN set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ENGINE_BUILD%\core\perfcache\laplace_t0_perfcache.bin"
if not defined LAPLACE_HIGHWAY_PERFCACHE_BIN set "LAPLACE_HIGHWAY_PERFCACHE_BIN=%LAPLACE_ENGINE_BUILD%\core\perfcache\laplace_highway_perfcache.bin"
if not defined LAPLACE_CHESS_POSITION_PERFCACHE_BIN set "LAPLACE_CHESS_POSITION_PERFCACHE_BIN=%LAPLACE_ENGINE_BUILD%\core\perfcache\laplace_chess_position_perfcache.bin"
rem Extension deploy MUST stay outside PGDATA (fsync/sharing-violation if under D:\Data\Postgres\laplace).
if not defined LAPLACE_PGDATA set "LAPLACE_PGDATA=%LAPLACE_DATA_ROOT%\pgdata"
if not defined INGEST if defined LAPLACE_DATA set "INGEST=%LAPLACE_DATA%"

rem UCD seed inputs live with ingest data, not under the build tree.
if not defined LAPLACE_UCD_ROOT set "LAPLACE_UCD_ROOT=%INGEST%\UCD\Public\UCD\latest"
if not defined REPOS if defined LAPLACE_SRC set "REPOS=%LAPLACE_SRC%"

if not defined LAPLACE_MODEL_HUB set "LAPLACE_MODEL_HUB=%LAPLACE_DATA_ROOT%\models\hub"

set "LAPLACE_ENV_LOADED=1"
exit /b 0
