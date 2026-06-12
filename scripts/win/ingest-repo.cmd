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
rem Sequencing law: a running deposit pins bin\Release for its duration. Build
rem engine/extension trees freely meanwhile; app builds wait for the deposit.
dotnet build "%LAPLACE_ROOT%\app\Laplace.Cli\Laplace.Cli.csproj" -c Release -v q --nologo || exit /b 1
echo ==== ingest repo %~1 ====
dotnet "%LAPLACE_ROOT%\app\Laplace.Cli\bin\Release\net10.0\Laplace.Cli.dll" ingest repo "%~1" || exit /b 1
