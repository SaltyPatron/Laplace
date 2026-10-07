@echo off
rem laplace-uci as a standalone chess engine, the way a conductor (fastchess, cutechess) starts it: published to
rem %LAPLACE_TOOLS%\chess\app (the Windows prefix laplace-uci check reads) with the engine DLLs it loads and
rem their run-time dependencies beside it. laplace-uci computes content IDs through laplace_core (BLAKE3), and Windows
rem searches a process's DLLs beside the executable, not on a dev shell's PATH: without them the engine fails its
rem type initializer ("Unable to load DLL 'laplace_core'") outside scripts\win\env.cmd or load-env.ps1.
rem   publish-uci.cmd [destination]
setlocal
set "HERE=%~dp0"
call "%HERE%env.cmd"
set "DEST=%~1"
if "%DEST%"=="" set "DEST=%LAPLACE_TOOLS%\chess\app"
cd /d "%LAPLACE_ROOT%" || exit /b 1
echo ==== [publish-uci] laplace-uci -^> %DEST% ====
dotnet publish app\Laplace.Chess.Uci\Laplace.Chess.Uci.csproj -c Release --no-self-contained -o "%DEST%" -v minimal --nologo || exit /b 1
rem the same four engine DLLs publish.cmd puts beside the site
for %%D in (core\laplace_core dynamics\laplace_dynamics synthesis\laplace_synthesis core\laplace_syzygy) do (
  if not exist "%LAPLACE_ENGINE_BUILD%\%%D.dll" (
    echo [publish-uci] ERROR: missing %LAPLACE_ENGINE_BUILD%\%%D.dll - run build-engine.cmd
    exit /b 1
  )
  copy /y "%LAPLACE_ENGINE_BUILD%\%%D.dll" "%DEST%\" >nul || exit /b 1
)
call "%HERE%engine-runtime.cmd" "%DEST%" || exit /b 1
rem the proof: a UCI handshake from a clean environment (no dev shell PATH), which is how a conductor runs it
pwsh -NoProfile -Command "$env:PATH = [Environment]::GetEnvironmentVariable('PATH','Machine'); $psi = [Diagnostics.ProcessStartInfo]::new('%DEST%\laplace-uci.exe'); $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $p = [Diagnostics.Process]::Start($psi); $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync(); $p.StandardInput.Write(\"uci`nquit`n\"); $p.StandardInput.Close(); if (-not $p.WaitForExit(30000)) { $p.Kill(); throw 'laplace-uci did not exit' }; if ($p.ExitCode -or $o.Result -notmatch 'uciok') { Write-Host $e.Result; throw \"laplace-uci handshake failed (exit $($p.ExitCode))\" }; 'laplace-uci: uciok from a clean environment'" || exit /b 1
echo OK laplace-uci at %DEST%
exit /b 0
