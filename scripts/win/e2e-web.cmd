@echo off
setlocal
call "%~dp0env.cmd"

set "SKIP_BUILD="
if /i "%~1"=="--skip-build" (
    set "SKIP_BUILD=1"
    shift
)

if not defined SKIP_BUILD (
    call "%~dp0build-web.cmd" --skip-install
    if errorlevel 1 exit /b 1
    dotnet build "%LAPLACE_ROOT%\app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj" -c Release -v minimal --nologo
    if errorlevel 1 exit /b 1
)

set "REUSE="
powershell -NoProfile -Command "try { Invoke-RestMethod http://127.0.0.1:5187/health -TimeoutSec 2 | Out-Null; exit 0 } catch { exit 1 }"
if not errorlevel 1 set "REUSE=1"

set "ENDPOINT_PID="
if not defined REUSE (
    echo ==== starting endpoint on :5187 ====
    set "LAPLACE_BILLING_BYPASS=true"
    set "ASPNETCORE_URLS=http://127.0.0.1:5187"
    for /f %%p in ('powershell -NoProfile -Command "$p = Start-Process dotnet -ArgumentList 'bin\Release\net10.0\Laplace.Endpoints.OpenAICompat.dll' -WorkingDirectory '%LAPLACE_ROOT%\app\Laplace.Endpoints.OpenAICompat' -PassThru -WindowStyle Hidden; $p.Id"') do set "ENDPOINT_PID=%%p"
    powershell -NoProfile -Command "for ($i=0; $i -lt 30; $i++) { try { Invoke-RestMethod http://127.0.0.1:5187/health -TimeoutSec 2 | Out-Null; exit 0 } catch { Start-Sleep -Milliseconds 500 } }; exit 1"
    if errorlevel 1 (
        echo [e2e-web] endpoint failed to come up
        if defined ENDPOINT_PID taskkill /pid %ENDPOINT_PID% /f >nul 2>&1
        exit /b 1
    )
)

cd /d "%LAPLACE_ROOT%\web"
call npx playwright test %1 %2 %3 %4 %5
set "RESULT=%ERRORLEVEL%"

if defined ENDPOINT_PID taskkill /pid %ENDPOINT_PID% /f >nul 2>&1
exit /b %RESULT%
