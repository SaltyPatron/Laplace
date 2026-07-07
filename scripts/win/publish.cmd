@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "PUBLISH_OUT=%LAPLACE_PUBLISH_ENDPOINT%"
set "CONFIG=Release"

:parse_args
if "%~1"=="" goto done_args
if /i "%~1"=="--no-build" (
  echo [publish] ERROR: --no-build is not supported — publish always builds Release.
  exit /b 2
)
if /i "%~1"=="--no-restore" (
  echo [publish] ERROR: --no-restore is not supported — publish always restores and builds Release.
  exit /b 2
)
if /i "%~1"=="--skip-managed-build" (
  set "SKIP_MANAGED_BUILD=1"
  shift
  goto parse_args
)
if /i "%~1"=="--configuration" (
  if /i not "%~2"=="Release" (
    echo [publish] ERROR: only -c Release is supported ^(got %~2^).
    exit /b 2
  )
  shift
  shift
  goto parse_args
)
if /i "%~1"=="-c" (
  if /i not "%~2"=="Release" (
    echo [publish] ERROR: only -c Release is supported ^(got %~2^).
    exit /b 2
  )
  shift
  shift
  goto parse_args
)
echo [publish] ERROR: unknown argument %~1
exit /b 2
:done_args

if not defined SKIP_MANAGED_BUILD (
  echo ==== [1/6] build deploy projects Release ====
  dotnet build app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj -c %CONFIG% -v minimal --nologo || exit /b 1
  dotnet build app\Laplace.Chess.Uci\Laplace.Chess.Uci.csproj -c %CONFIG% -v minimal --nologo || exit /b 1
  dotnet build app\Laplace.Migrations\Laplace.Migrations.csproj -c %CONFIG% -v minimal --nologo || exit /b 1
) else (
  echo ==== [1/6] build deploy projects Release [skipped: --skip-managed-build] ====
)

echo ==== [2/6] build SPA ^(web/dist^) ====
call "%~dp0build-web.cmd" --skip-install || exit /b 1

echo ==== [3/6] publish API -^> %PUBLISH_OUT% ====
if exist "%PUBLISH_OUT%" rmdir /s /q "%PUBLISH_OUT%"
dotnet publish app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj ^
    -c %CONFIG% --no-self-contained -o "%PUBLISH_OUT%"
if errorlevel 1 (
  echo [publish] FAILED
  exit /b 1
)

echo ==== [4/6] publish laplace-uci beside endpoint ====
set "UCI_STAGE=%TEMP%\laplace-uci-publish"
if exist "%UCI_STAGE%" rmdir /s /q "%UCI_STAGE%"
dotnet publish app\Laplace.Chess.Uci\Laplace.Chess.Uci.csproj ^
    -c %CONFIG% --no-self-contained -o "%UCI_STAGE%"
if errorlevel 1 (
  echo [publish] laplace-uci FAILED
  exit /b 1
)
robocopy "%UCI_STAGE%" "%PUBLISH_OUT%" /E /NFL /NDL /NJH /NJS /NP
if errorlevel 8 exit /b 1
if exist "%UCI_STAGE%" rmdir /s /q "%UCI_STAGE%"
if not exist "%PUBLISH_OUT%\laplace-uci.exe" (
  echo [publish] ERROR: laplace-uci.exe missing from %PUBLISH_OUT%
  exit /b 1
)

echo ==== [5/6] overlay web/dist + native DLLs ====
robocopy "%LAPLACE_ROOT%\web\dist" "%PUBLISH_OUT%\wwwroot" /MIR /NJH /NJS /NDL /NFL
if errorlevel 8 exit /b 1
for %%D in (core dynamics synthesis) do (
  if not exist "%LAPLACE_ENGINE_BUILD%\%%D\laplace_%%D.dll" (
    echo [publish] ERROR: missing %LAPLACE_ENGINE_BUILD%\%%D\laplace_%%D.dll — run build-engine.cmd or publish-deploy.cmd --full
    exit /b 1
  )
  copy /y "%LAPLACE_ENGINE_BUILD%\%%D\laplace_%%D.dll" "%PUBLISH_OUT%\" >nul
)
if exist "C:\Program Files\PostgreSQL\18\bin\libxml2.dll" (
  copy /y "C:\Program Files\PostgreSQL\18\bin\libxml2.dll" "%PUBLISH_OUT%\" >nul
)

echo ==== [6/6] publish migrations ====
dotnet publish app\Laplace.Migrations\Laplace.Migrations.csproj ^
    -c %CONFIG% -o "%LAPLACE_PUBLISH_MIGRATIONS%"
if errorlevel 1 (
  echo [publish] migrations FAILED
  exit /b 1
)

call :require_file "%PUBLISH_OUT%\Laplace.Endpoints.OpenAICompat.dll" || exit /b 1
call :require_file "%PUBLISH_OUT%\Laplace.Core.dll" || exit /b 1
call :require_file "%PUBLISH_OUT%\Laplace.Chess.dll" || exit /b 1
call :require_file "%PUBLISH_OUT%\Laplace.Substrate.dll" || exit /b 1

echo.
echo [publish] OK
echo   endpoint:   %PUBLISH_OUT%\Laplace.Endpoints.OpenAICompat.dll
echo   migrations: %LAPLACE_PUBLISH_MIGRATIONS%\Laplace.Migrations.dll
echo   next:       scripts\win\deploy-api.cmd
exit /b 0

:require_file
if not exist "%~1" (
  echo [publish] ERROR: missing %~1
  exit /b 1
)
exit /b 0
