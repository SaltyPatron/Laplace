@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\app"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace"
set "LAPLACE_PERFCACHE_BIN=%LAPLACE_ROOT%\build-win\core\perfcache\laplace_t0_perfcache.bin"
set "SIDECAR=%TEMP%\laplace-cli-sidecar"
if not exist "%SIDECAR%\Laplace.Cli.dll" dotnet build Laplace.Cli\Laplace.Cli.csproj -c Release -v q --nologo -o "%SIDECAR%" || exit /b 1
for %%f in (%*) do (
  echo ==== db-roundtrip %%f ====
  dotnet "%SIDECAR%\Laplace.Cli.dll" db-roundtrip "%%~f" || exit /b 1
)
