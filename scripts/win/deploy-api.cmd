@echo off
setlocal
call "%~dp0env.cmd"





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


rem Only LaplacePool's own worker blocks the copy. The old check matched ANY w3wp.exe on
rem the box (every IIS app pool), so it aborted even with LaplacePool stopped whenever some
rem unrelated site had a worker up — the reason deploy-api kept silently no-op'ing.
set "APPCMD=%SystemRoot%\System32\inetsrv\appcmd.exe"
"%APPCMD%" list wp 2>nul | "%SystemRoot%\System32\find.exe" /i "LaplacePool" >nul
if not errorlevel 1 (
    echo [deploy-api] LaplacePool worker is RUNNING — stop the LaplacePool app pool first, then re-run this.
    exit /b 1
)

echo ==== Copying managed endpoint (preserving live web.config) ====

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
