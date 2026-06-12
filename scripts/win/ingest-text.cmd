@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
rem Connection/data-path constants come from env.cmd (single source; pre-set to override).
rem Sequencing law: a running deposit pins bin\Release for its duration. Build
rem engine/extension trees freely meanwhile; app builds wait for the deposit.
dotnet build "%LAPLACE_ROOT%\app\Laplace.Cli\Laplace.Cli.csproj" -c Release -v q --nologo || exit /b 1
for %%f in (%*) do (
  echo ==== db-roundtrip %%f ====
  dotnet "%LAPLACE_ROOT%\app\Laplace.Cli\bin\Release\net10.0\Laplace.Cli.dll" db-roundtrip "%%~f" || exit /b 1
)
