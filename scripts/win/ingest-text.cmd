@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
rem Connection/data-path constants come from env.cmd (single source; pre-set to override).
call "%~dp0cli-sidecar.cmd" || exit /b 1
for %%f in (%*) do (
  echo ==== db-roundtrip %%f ====
  dotnet "%SIDECAR%\Laplace.Cli.dll" db-roundtrip "%%~f" || exit /b 1
)
