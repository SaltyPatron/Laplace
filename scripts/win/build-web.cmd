@echo off
setlocal
rem ==== Laplace web SPA build ===============================================
rem install deps -> regenerate API types from the committed OpenAPI doc ->
rem vite build -> mirror dist into the endpoint's wwwroot (served by Kestrel).
rem
rem   build-web.cmd [--skip-install]
rem ==========================================================================
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%\web"

if /i "%~1"=="--skip-install" goto :gen
echo ==== npm install ====
call npm install --no-fund --no-audit
if errorlevel 1 exit /b 1

:gen
echo ==== generating API types from openapi\openapi.json ====
call npm run gen:api
if errorlevel 1 exit /b 1

echo ==== vite build ====
call npm run build
if errorlevel 1 exit /b 1

echo ==== mirroring dist to endpoint wwwroot ====
robocopy "%LAPLACE_ROOT%\web\dist" "%LAPLACE_ROOT%\app\Laplace.Endpoints.OpenAICompat\wwwroot" /MIR /NJH /NJS /NDL /NFL
if errorlevel 8 exit /b 1

echo.
echo [build-web] OK — SPA staged at app\Laplace.Endpoints.OpenAICompat\wwwroot
exit /b 0
