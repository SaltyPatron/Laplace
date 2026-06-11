@echo off
rem Build (or refresh) the CLI sidecar at %TEMP%\laplace-cli-sidecar and export %SIDECAR%.
rem The sidecar exists so long-running ingests never lock app\...\bin\Release for builders
rem (concurrent-runner law). STALENESS is checked against app\ sources -- the old pattern
rem ("build only if dll missing") served stale CLIs after C# changes and burned a session.
rem No setlocal: SIDECAR is exported to the caller. Caller must have run env.cmd.
set "SIDECAR=%TEMP%\laplace-cli-sidecar"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$dll = Join-Path $env:SIDECAR 'Laplace.Cli.dll';" ^
  "if (-not (Test-Path $dll)) { exit 1 };" ^
  "$built = (Get-Item $dll).LastWriteTime;" ^
  "$newest = (Get-ChildItem (Join-Path $env:LAPLACE_ROOT 'app') -Recurse -Include *.cs,*.csproj | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime;" ^
  "if ($newest -gt $built) { exit 1 } else { exit 0 }"
if not errorlevel 1 exit /b 0
echo ==== building CLI sidecar (missing or stale) ====
dotnet build "%LAPLACE_ROOT%\app\Laplace.Cli\Laplace.Cli.csproj" -c Release -v q --nologo -o "%SIDECAR%" || exit /b 1
exit /b 0
