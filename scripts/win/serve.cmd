@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

echo ==== Starting Laplace endpoint (defaults from laplace.env / built-in) ====
echo      http://127.0.0.1:5187/v1
echo.
dotnet run --project app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj -c Release
