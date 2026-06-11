@echo off
setlocal
rem ==== Ingest a source repo/dir as code testimony (RepoDecomposer) =============
rem   scripts\win\ingest-repo.cmd <path-to-repo-or-dir>
rem Produces AST entities + content trajectories + PRECEDES + DEFINES/CALLS/REFERENCES
rem for every recognized source file (C/C++/C#/Python/Rust/Go/...).
rem ==============================================================================
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
rem Connection/data-path constants come from env.cmd (single source; pre-set to override).
call "%~dp0cli-sidecar.cmd" || exit /b 1
echo ==== ingest repo %~1 ====
dotnet "%SIDECAR%\Laplace.Cli.dll" ingest repo "%~1" || exit /b 1
