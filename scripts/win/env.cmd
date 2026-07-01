@echo off
set "NoDefaultCurrentDirectoryInExePath="
set "PSModulePath="
call "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" >nul 2>&1
set "Platform="
setlocal EnableDelayedExpansion
set "_LIB="
for %%D in ("!LIB:;=" "!") do (if not "%%~D"=="" if exist "%%~D\" set "_LIB=!_LIB!%%~D;")
endlocal & set "LIB=%_LIB%"
set "LAPLACE_ROOT=%~dp0..\.."
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
set "PATH=%PGBIN%;%PATH%"
set "PATH=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64;%PATH%"
set "PATH=D:\Microsoft Visual Studio\2026\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja;D:\Microsoft Visual Studio\2026\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;%PATH%"
set "PATH=C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin;C:\Program Files (x86)\Intel\oneAPI\mkl\latest\bin;C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin;%PATH%"
set "PATH=%LAPLACE_ROOT%\build-win\core;%LAPLACE_ROOT%\build-win\dynamics;%LAPLACE_ROOT%\build-win\synthesis;%PATH%"
set "LAPLACE_RC=C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/rc.exe"
set "LAPLACE_MT=C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/mt.exe"
if not defined PGPASSWORD set "PGPASSWORD=postgres"
rem Production hosts must set PGPASSWORD (and LAPLACE_DB) explicitly — do not rely on this default.
if not defined LAPLACE_DBNAME set "LAPLACE_DBNAME=laplace"
if not defined LAPLACE_CANONICAL_DB set "LAPLACE_CANONICAL_DB=laplace"
if not defined LAPLACE_ISOLATE_PREFIX set "LAPLACE_ISOLATE_PREFIX=laplace_d"
if not defined LAPLACE_DB set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=%LAPLACE_DBNAME%;Command Timeout=0"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=0"
if not defined LAPLACE_SKIP_MODELS set "LAPLACE_SKIP_MODELS=0"
rem Ingest worker counts (file decompose, commit pool, apply partitions) are resolved at runtime
rem from CpuTopology in managed code — do NOT set LAPLACE_INGEST_WORKERS / LAPLACE_DECOMPOSE_WORKERS
rem in scripts unless you are deliberately overriding detection for a one-off experiment.
if not defined LAPLACE_INGEST_BATCH set "LAPLACE_INGEST_BATCH=65536"
rem One Hilbert-sorted bulk apply_batch per commit (compose still parallel on P-cores).
if not defined LAPLACE_APPLY_PARTITIONS set "LAPLACE_APPLY_PARTITIONS=1"
if not defined LAPLACE_TBB_MAX_THREADS_PER_CORE set "LAPLACE_TBB_MAX_THREADS_PER_CORE=1"
if not defined MKL_NUM_THREADS set "MKL_NUM_THREADS=8"
if not defined TBB_NUM_THREADS set "TBB_NUM_THREADS=8"
if not defined MKL_DYNAMIC set "MKL_DYNAMIC=0"
if not defined LAPLACE_NATIVE_THREADS set "LAPLACE_NATIVE_THREADS=8"
if not defined LAPLACE_PERFCACHE_BIN set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ROOT%\build-win\core\perfcache\laplace_t0_perfcache.bin"
if not defined INGEST set "INGEST=D:\Data\Ingest"
if not defined LAPLACE_DATA_ROOT set "LAPLACE_DATA_ROOT=%INGEST%"
if not defined REPOS set "REPOS=D:\Repositories"
if not defined LAPLACE_MODEL_HUB set "LAPLACE_MODEL_HUB=D:\Models\hub"
