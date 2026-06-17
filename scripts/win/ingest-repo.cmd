@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
dotnet build "%LAPLACE_ROOT%\app\Laplace.Cli\Laplace.Cli.csproj" -c Release -v q --nologo || exit /b 1
echo ==== ingest repo %~1 ====
dotnet "%LAPLACE_ROOT%\app\Laplace.Cli\bin\Release\net10.0\Laplace.Cli.dll" ingest repo "%~1" || exit /b 1
