@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "PUBLISH_OUT=%LAPLACE_PUBLISH_ENDPOINT%"
set "CONFIG=Release"
set "SKIP_MANAGED_BUILD="

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
  echo ==== [1/5] build OpenAICompat Release ^(openapi for web^) ====
  dotnet build app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj -c %CONFIG% -v minimal --nologo || exit /b 1
) else (
  echo ==== [1/5] build OpenAICompat Release [skipped: --skip-managed-build] ====
)

set "UCI_STAGE=%TEMP%\laplace-uci-publish"
set "WEB_LOG=%TEMP%\laplace-publish-web.log"
set "UCI_LOG=%TEMP%\laplace-publish-uci.log"
set "MIG_LOG=%TEMP%\laplace-publish-mig.log"

echo ==== [2/5] parallel: build-web || publish UCI || publish Migrations ====
if exist "%UCI_STAGE%" rmdir /s /q "%UCI_STAGE%"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$root='%LAPLACE_ROOT%'; $cfg='%CONFIG%'; $uciStage='%UCI_STAGE%'; $migOut='%LAPLACE_PUBLISH_MIGRATIONS%';" ^
  "$webLog='%WEB_LOG%'; $uciLog='%UCI_LOG%'; $migLog='%MIG_LOG%';" ^
  "$webCmd = 'call \"' + (Join-Path $root 'scripts\win\build-web.cmd') + '\" --skip-install';" ^
  "$web = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $webCmd) -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput $webLog -RedirectStandardError ($webLog+'.err');" ^
  "$uciArgs = @('publish','app\Laplace.Chess.Uci\Laplace.Chess.Uci.csproj','-c',$cfg,'--no-self-contained','-o',$uciStage);" ^
  "$uci = Start-Process -FilePath 'dotnet' -ArgumentList $uciArgs -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput $uciLog -RedirectStandardError ($uciLog+'.err');" ^
  "$migArgs = @('publish','app\Laplace.Migrations\Laplace.Migrations.csproj','-c',$cfg,'-o',$migOut);" ^
  "$mig = Start-Process -FilePath 'dotnet' -ArgumentList $migArgs -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput $migLog -RedirectStandardError ($migLog+'.err');" ^
  "Wait-Process -Id $web.Id,$uci.Id,$mig.Id;" ^
  "foreach ($pair in @(@('web',$webLog,$web),@('uci',$uciLog,$uci),@('migrations',$migLog,$mig))) {" ^
  "  Write-Host ('---- '+$pair[0]+' ----'); Get-Content $pair[1],($pair[1]+'.err') -ErrorAction SilentlyContinue;" ^
  "  if ($pair[2].ExitCode -ne 0) { Write-Host ('FAIL '+$pair[0]+' rc='+$pair[2].ExitCode); exit 1 }" ^
  "}; exit 0"
if errorlevel 1 (
  echo [publish] parallel web/UCI/migrations FAILED
  exit /b 1
)

echo ==== [3/5] publish API -^> %PUBLISH_OUT% ====
if exist "%PUBLISH_OUT%" rmdir /s /q "%PUBLISH_OUT%"
if defined SKIP_MANAGED_BUILD (
  dotnet publish app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj ^
      -c %CONFIG% --no-self-contained -o "%PUBLISH_OUT%"
) else (
  rem OpenAICompat was built in step 1 — publish without rebuilding.
  dotnet publish app\Laplace.Endpoints.OpenAICompat\Laplace.Endpoints.OpenAICompat.csproj ^
      -c %CONFIG% --no-build --no-self-contained -o "%PUBLISH_OUT%"
)
if errorlevel 1 (
  echo [publish] FAILED
  exit /b 1
)

echo ==== [4/5] overlay UCI + web/dist + native DLLs ====
robocopy "%UCI_STAGE%" "%PUBLISH_OUT%" /E /NFL /NDL /NJH /NJS /NP
if errorlevel 8 exit /b 1
if exist "%UCI_STAGE%" rmdir /s /q "%UCI_STAGE%"
if not exist "%PUBLISH_OUT%\laplace-uci.exe" (
  echo [publish] ERROR: laplace-uci.exe missing from %PUBLISH_OUT%
  exit /b 1
)
robocopy "%LAPLACE_ROOT%\web\dist" "%PUBLISH_OUT%\wwwroot" /MIR /NJH /NJS /NDL /NFL
if errorlevel 8 exit /b 1
for %%D in (core dynamics synthesis) do (
  if not exist "%LAPLACE_ENGINE_BUILD%\%%D\laplace_%%D.dll" (
    echo [publish] ERROR: missing %LAPLACE_ENGINE_BUILD%\%%D\laplace_%%D.dll — run build-engine.cmd or publish-deploy.cmd --full
    exit /b 1
  )
  copy /y "%LAPLACE_ENGINE_BUILD%\%%D\laplace_%%D.dll" "%PUBLISH_OUT%\" >nul
)

echo ==== [5/5] verify ====
call :require_file "%PUBLISH_OUT%\Laplace.Endpoints.OpenAICompat.dll" || exit /b 1
call :require_file "%PUBLISH_OUT%\Laplace.Core.dll" || exit /b 1
call :require_file "%PUBLISH_OUT%\Laplace.Chess.dll" || exit /b 1
call :require_file "%PUBLISH_OUT%\Laplace.Substrate.dll" || exit /b 1
call :require_file "%LAPLACE_PUBLISH_MIGRATIONS%\Laplace.Migrations.dll" || exit /b 1

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
