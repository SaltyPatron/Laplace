@echo off
call "D:\Repositories\Laplace\scripts\win\env.cmd" >nul 2>&1
set "Platform="
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace"
set "LAPLACE_PERFCACHE_BIN=D:\Repositories\Laplace\build-win\core\perfcache\laplace_t0_perfcache.bin"
if not defined LAPLACE_INGEST_WORKERS set "LAPLACE_INGEST_WORKERS=8"
if not defined LAPLACE_DECOMPOSE_WORKERS set "LAPLACE_DECOMPOSE_WORKERS=10"
if not defined LAPLACE_FOLD_WORKERS set "LAPLACE_FOLD_WORKERS=8"
if not defined LAPLACE_INGEST_BATCH set "LAPLACE_INGEST_BATCH=2048"
if not defined LAPLACE_INGEST_COMMIT_ROWS set "LAPLACE_INGEST_COMMIT_ROWS=250000"
if not defined LAPLACE_STAGING_THRESHOLD set "LAPLACE_STAGING_THRESHOLD=20000000"
cd /d D:\Repositories\Laplace
%*
