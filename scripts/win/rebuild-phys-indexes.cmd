@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
echo ensuring missing physicalities indexes (CREATE IF NOT EXISTS only) ...
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- rebuild-phys-indexes
exit /b %ERRORLEVEL%
