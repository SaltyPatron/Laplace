@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
if not exist "%LAPLACE_CLI_EXE%" (
  dotnet build "%LAPLACE_ROOT%\app\Laplace.Cli\Laplace.Cli.csproj" -c Release -v q --nologo || exit /b 1
)
for %%f in (%*) do (
  echo ==== db-roundtrip %%f ====
  "%LAPLACE_CLI_EXE%" db-roundtrip "%%~f" || exit /b 1
)
