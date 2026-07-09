@echo off
if defined LAPLACE_ENV_LOADED exit /b 0
set "LAPLACE_ENV_LOADED=1"
set "NoDefaultCurrentDirectoryInExePath="
set "PSModulePath="
call "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" >nul 2>&1
set "Platform="
setlocal EnableDelayedExpansion
set "_LIB="
for %%D in ("!LIB:;=" "!") do (if not "%%~D"=="" if exist "%%~D\" set "_LIB=!_LIB!%%~D;")
endlocal & set "LIB=%_LIB%"
set "LAPLACE_ROOT=%~dp0..\.."
rem All build/deploy/publish artifacts live under D:\Data\Laplace — never in the repo tree.
if not defined LAPLACE_DATA_ROOT set "LAPLACE_DATA_ROOT=D:\Data\Laplace"
if not defined LAPLACE_BUILD_ROOT set "LAPLACE_BUILD_ROOT=%LAPLACE_DATA_ROOT%"
if not defined LAPLACE_DEPLOY set "LAPLACE_DEPLOY=%LAPLACE_DATA_ROOT%\deploy"
if not defined LAPLACE_ENGINE_BUILD set "LAPLACE_ENGINE_BUILD=%LAPLACE_BUILD_ROOT%\build-win"
if not defined LAPLACE_EXT_BUILD set "LAPLACE_EXT_BUILD=%LAPLACE_BUILD_ROOT%\build-win-ext"
if not defined LAPLACE_ENGINE_BUILD_ASAN set "LAPLACE_ENGINE_BUILD_ASAN=%LAPLACE_BUILD_ROOT%\build-win-asan"
if not defined LAPLACE_CUTECHESS_BUILD set "LAPLACE_CUTECHESS_BUILD=%LAPLACE_BUILD_ROOT%\build-cutechess"
if not defined LAPLACE_OUT set "LAPLACE_OUT=%LAPLACE_BUILD_ROOT%\out"
if not defined LAPLACE_PUBLISH_ENDPOINT set "LAPLACE_PUBLISH_ENDPOINT=%LAPLACE_OUT%\endpoint"
if not defined LAPLACE_PUBLISH_MIGRATIONS set "LAPLACE_PUBLISH_MIGRATIONS=%LAPLACE_OUT%\migrations"
if not defined LAPLACE_IIS_API set "LAPLACE_IIS_API=D:\Data\inetsrv\laplace-api"
if not defined LAPLACE_CLI_EXE set "LAPLACE_CLI_EXE=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Cli\Release\net10.0\Laplace.Cli.exe"
if not defined LAPLACE_CLI_DLL set "LAPLACE_CLI_DLL=%LAPLACE_BUILD_ROOT%\app\bin\Laplace.Cli\Release\net10.0\Laplace.Cli.dll"
set "LAPLACE_DEPLOY_PG=%LAPLACE_DEPLOY:\=/%"
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
set "PATH=%PGBIN%;%PATH%"
set "PATH=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64;%PATH%"
set "PATH=D:\Microsoft Visual Studio\2026\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja;D:\Microsoft Visual Studio\2026\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;%PATH%"
set "PATH=C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin;C:\Program Files (x86)\Intel\oneAPI\mkl\latest\bin;C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin;%PATH%"
set "PATH=%LAPLACE_ENGINE_BUILD%\core;%LAPLACE_ENGINE_BUILD%\dynamics;%LAPLACE_ENGINE_BUILD%\synthesis;%PATH%"
set "LAPLACE_RC=C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/rc.exe"
set "LAPLACE_MT=C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/mt.exe"
if not defined PGPASSWORD set "PGPASSWORD=postgres"

if not defined LAPLACE_DBNAME set "LAPLACE_DBNAME=laplace"
if not defined LAPLACE_CANONICAL_DB set "LAPLACE_CANONICAL_DB=laplace"
if not defined LAPLACE_ISOLATE_PREFIX set "LAPLACE_ISOLATE_PREFIX=laplace_d"
rem LAPLACE_PGHOST/LAPLACE_PGUSER feed BOTH the CLI connection string below AND
rem every scripted psql verify/health check, so the writer and the verifier can
rem never target different databases. Remote seeding (e.g. Windows -> hart-server):
rem set LAPLACE_PGHOST=hart-server before calling any seed script.
if not defined LAPLACE_PGHOST set "LAPLACE_PGHOST=localhost"
if not defined LAPLACE_PGUSER set "LAPLACE_PGUSER=postgres"
if not defined LAPLACE_DB set "LAPLACE_DB=Host=%LAPLACE_PGHOST%;Username=%LAPLACE_PGUSER%;Password=%PGPASSWORD%;Database=%LAPLACE_DBNAME%;Command Timeout=0"
if not defined LAPLACE_BILLING_BYPASS set "LAPLACE_BILLING_BYPASS=true"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=0"
if not defined LAPLACE_SKIP_MODELS set "LAPLACE_SKIP_MODELS=0"




rem Ingest batch/commit/worker counts are derived at runtime from Intel topology
rem (CpuTopology) + RAM (IngestSizing.ResolveForSource). Do not set LAPLACE_INGEST_* here.

if not defined LAPLACE_TBB_MAX_THREADS_PER_CORE set "LAPLACE_TBB_MAX_THREADS_PER_CORE=1"
rem MKL/TBB/native thread counts are reconciled from Intel P-core topology at CLI startup
rem (NativeRuntimeEnv.ApplyFromTopology). Values here are fallbacks for non-CLI tools only.
if not defined MKL_NUM_THREADS set "MKL_NUM_THREADS=8"
if not defined TBB_NUM_THREADS set "TBB_NUM_THREADS=8"
if not defined MKL_DYNAMIC set "MKL_DYNAMIC=0"
if not defined LAPLACE_NATIVE_THREADS set "LAPLACE_NATIVE_THREADS=8"
REM Server GC for the ingest CLI: measured live (Lumbras chess, 7 compose workers)
REM at 1.9GB/s allocation rate, 73 gen0 + 29 gen1 collections/s, 16% of wall time
REM in GC pause under the default workstation GC — every collection suspends all
REM pinned workers at once. Server GC = per-core heaps + parallel collection.
REM Heap count capped to the P-core budget so 32 logical procs don't inflate RSS.
if not defined DOTNET_gcServer set "DOTNET_gcServer=1"
if not defined DOTNET_GCHeapCount set "DOTNET_GCHeapCount=8"
if not defined LAPLACE_PERFCACHE_BIN set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ENGINE_BUILD%\core\perfcache\laplace_t0_perfcache.bin"
if not defined LAPLACE_HIGHWAY_PERFCACHE_BIN set "LAPLACE_HIGHWAY_PERFCACHE_BIN=%LAPLACE_ENGINE_BUILD%\core\perfcache\laplace_highway_perfcache.bin"
rem Extension deploy MUST stay outside PGDATA (fsync/sharing-violation if under D:\Data\Postgres\laplace).
if not defined LAPLACE_PGDATA set "LAPLACE_PGDATA=D:\Data\Postgres"
if not defined INGEST set "INGEST=D:\Data\Ingest"
rem UCD seed inputs live with ingest data, not under the build tree.
if not defined LAPLACE_UCD_ROOT set "LAPLACE_UCD_ROOT=%INGEST%\UCD\Public\UCD\latest"
if not defined REPOS set "REPOS=D:\Repositories"
if not defined LAPLACE_MODEL_HUB set "LAPLACE_MODEL_HUB=D:\Models\hub"

exit /b 0
