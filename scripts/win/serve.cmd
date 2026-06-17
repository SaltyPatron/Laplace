@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

if not defined LAPLACE_PORT (
    set "LAPLACE_PORT=5187"
)

set "LAPLACE_BILLING_BYPASS=true"
set "ASPNETCORE_URLS=http://0.0.0.0:%LAPLACE_PORT%"

echo ==== Starting Laplace endpoint on http://localhost:%LAPLACE_PORT%/v1 ====
echo      LAPLACE_BILLING_BYPASS=true  (no quote header required)
echo      DB: %LAPLACE_DB%
echo.
dotnet run --project app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj -c Release
