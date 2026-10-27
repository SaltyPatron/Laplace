@echo off
setlocal
call "%~dp0env.cmd" || exit /b 1
cd /d "%LAPLACE_ROOT%\web"

set "BUILD_OPTIONS="
:parse
if "%~1"=="" goto build
if /i "%~1"=="--skip-install" ( set "BUILD_OPTIONS=%BUILD_OPTIONS% --skip-install" & shift /1 & goto parse )
if /i "%~1"=="--force" ( set "BUILD_OPTIONS=%BUILD_OPTIONS% --force" & shift /1 & goto parse )
echo build-web: unknown option %~1
exit /b 2
:build
python "%LAPLACE_ROOT%\scripts\web-artifact.py" build --root "%LAPLACE_ROOT%" --manifest "%LAPLACE_BUILD_ROOT%\iis-publish\web-build.json" %BUILD_OPTIONS%
if errorlevel 1 exit /b 1

echo ==== mirroring dist to endpoint wwwroot ====
robocopy "%LAPLACE_ROOT%\web\dist" "%LAPLACE_ROOT%\app\Laplace.Endpoints.OpenAICompat\wwwroot" /MIR /NJH /NJS /NDL /NFL
if errorlevel 8 exit /b 1

echo.
echo [build-web] OK — SPA staged at app\Laplace.Endpoints.OpenAICompat\wwwroot
exit /b 0
