@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
if not exist "%LAPLACE_ROOT%\build-win\deferred-phys-index-defs.json" (
  echo no deferred physicalities indexes to rebuild
  exit /b 0
)
echo rebuilding deferred physicalities indexes ...
dotnet run --project Laplace.Cli\Laplace.Cli.csproj -c Release --no-build -- rebuild-phys-indexes
exit /b %ERRORLEVEL%
