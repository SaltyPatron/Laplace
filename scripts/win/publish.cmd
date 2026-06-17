@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "PUBLISH_OUT=%LAPLACE_ROOT%\out\endpoint"

echo ==== Publishing Laplace.Endpoints.OpenAICompat to %PUBLISH_OUT% ====
dotnet publish app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj ^
    -c Release -o "%PUBLISH_OUT%" %*
if errorlevel 1 (
    echo [publish] FAILED
    exit /b 1
)

echo ==== Publishing Laplace.Migrations to %LAPLACE_ROOT%\out\migrations ====
dotnet publish app\Laplace.Migrations\Laplace.Migrations.csproj ^
    -c Release -o "%LAPLACE_ROOT%\out\migrations"
if errorlevel 1 (
    echo [publish] migrations FAILED
    exit /b 1
)

echo.
echo [publish] OK
echo   endpoint:   %PUBLISH_OUT%\Laplace.Endpoints.OpenAICompat.dll
echo   migrations: %LAPLACE_ROOT%\out\migrations\Laplace.Migrations.dll
echo   run:        dotnet %PUBLISH_OUT%\Laplace.Endpoints.OpenAICompat.dll
exit /b 0
