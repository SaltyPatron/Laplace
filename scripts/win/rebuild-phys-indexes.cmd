@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
echo ensuring missing physicalities indexes (CREATE IF NOT EXISTS only) ...
if not exist "%LAPLACE_CLI_EXE%" (
  dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q --nologo || exit /b 1
)
"%LAPLACE_CLI_EXE%" rebuild-phys-indexes
exit /b %ERRORLEVEL%
