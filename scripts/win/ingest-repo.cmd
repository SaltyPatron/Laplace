@echo off
setlocal
rem ==== Ingest a source repo/dir as code testimony (RepoDecomposer) =============
rem   scripts\win\ingest-repo.cmd <path-to-repo-or-dir>
rem Produces AST entities + content trajectories + PRECEDES + DEFINES/CALLS/REFERENCES
rem for every recognized source file (C/C++/C#/Python/Rust/Go/...).
rem ==============================================================================
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
if not defined LAPLACE_DB set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace"
set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ROOT%\build-win\core\perfcache\laplace_t0_perfcache.bin"
set "SIDECAR=%TEMP%\laplace-cli-sidecar"
if not exist "%SIDECAR%\Laplace.Cli.dll" dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q --nologo -o "%SIDECAR%" || exit /b 1
echo ==== ingest repo %~1 ====
dotnet "%SIDECAR%\Laplace.Cli.dll" ingest repo "%~1" || exit /b 1
