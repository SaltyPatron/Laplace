@echo off
setlocal
call "%~dp0env.cmd"

rem Deploy the staged endpoint (out\endpoint) to the live IIS folder. RUN ONLY WITH THE APP POOL STOPPED
rem (a running w3wp locks the DLLs). Preserves the live web.config (its LAPLACE_DB/LAPLACE_CHESS_DB must stay
rem = laplace) and refreshes the native DLLs that `dotnet publish` does NOT copy (laplace_core/dynamics/synthesis).

set "SRC=%LAPLACE_ROOT%\out\endpoint"
set "LIVE=D:\Data\inetsrv\laplace-api"

if not exist "%SRC%\Laplace.Endpoints.OpenAICompat.dll" (
    echo [deploy-api] no staged build at %SRC% — run scripts\win\publish.cmd first
    exit /b 1
)
if not exist "%LIVE%\" (
    echo [deploy-api] live folder %LIVE% not found
    exit /b 1
)

rem Refuse to run while the pool is up (would fail on locked DLLs and half-copy).
tasklist /fi "imagename eq w3wp.exe" | find /i "w3wp.exe" >nul
if not errorlevel 1 (
    echo [deploy-api] w3wp is RUNNING — stop the laplace-api app pool first, then re-run this.
    exit /b 1
)

echo ==== Copying managed endpoint (preserving live web.config) ====
rem /E recurse, /XF web.config keeps the live config (env vars), /XO skip older, /NJH /NJS quiet headers.
robocopy "%SRC%" "%LIVE%" /E /XF web.config /NFL /NDL /NJH /NJS /NP
if errorlevel 8 ( echo [deploy-api] robocopy endpoint FAILED & exit /b 1 )

echo ==== Refreshing native DLLs (publish does not copy these) ====
for %%D in (core dynamics synthesis) do (
    for %%F in ("%LAPLACE_ROOT%\build-win\%%D\laplace_%%D.dll") do (
        if exist "%%~F" ( copy /y "%%~F" "%LIVE%\" >nul && echo   laplace_%%D.dll ) else ( echo   [warn] missing %%~F )
    )
)

echo.
echo [deploy-api] OK — %LIVE% updated. Start the laplace-api app pool to go live.
echo   verify: curl -s http://localhost:8080/chess/new
exit /b 0
