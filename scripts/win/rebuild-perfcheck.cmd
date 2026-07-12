@echo off
rem rebuild-perfcheck.cmd — assert T0 + highway perfcache blobs exist after engine build.
setlocal
call "%~dp0env.cmd"

if not exist "%LAPLACE_PERFCACHE_BIN%" (
  echo ERROR: T0 perfcache blob missing at %LAPLACE_PERFCACHE_BIN%
  echo        engine ALL build should have emitted it — check laplace_t0_perfcache target
  exit /b 1
)
if not exist "%LAPLACE_HIGHWAY_PERFCACHE_BIN%" (
  echo ERROR: highway perfcache blob missing at %LAPLACE_HIGHWAY_PERFCACHE_BIN%
  echo        engine ALL build should have emitted it — check laplace_highway_perfcache target
  exit /b 1
)
for %%F in ("%LAPLACE_PERFCACHE_BIN%") do echo T0 perfcache ready: %%~zF bytes — %%F
for %%F in ("%LAPLACE_HIGHWAY_PERFCACHE_BIN%") do echo highway perfcache ready: %%~zF bytes — %%F
exit /b 0
