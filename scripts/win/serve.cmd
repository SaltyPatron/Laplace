@echo off
setlocal
rem ==== Laplace OpenAI-compatible endpoint (local dev) =======================
rem Starts the substrate inference server on http://localhost:5187
rem with billing bypassed so no quote header is required.
rem
rem   LAPLACE_DB        connection string (default: localhost/postgres)
rem   LAPLACE_PORT      port override    (default: 5187)
rem
rem Wires into VS Code via .continue/config.json or any OpenAI-compatible
rem client pointing at http://localhost:5187/v1
rem ==========================================================================
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

if not defined LAPLACE_DB (
    set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace"
)
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
