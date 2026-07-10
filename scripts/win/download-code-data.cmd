@echo off
setlocal
call "%~dp0env.cmd"

if "%1"=="" (
    echo Usage: download-code-data.cmd ^<tiny-codes ^| stack-v2 ^| authority^> [options]
    exit /b 1
)

if /i "%1"=="authority" (
    setlocal EnableDelayedExpansion
    set "AUTH=!INGEST!\code-authority"
    if not exist "!AUTH!" mkdir "!AUTH!"
    echo ==== clone authority repos in parallel ====
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
      "$ErrorActionPreference='Stop';" ^
      "$auth='%AUTH%';" ^
      "$repos=@('postgres/postgres','python/cpython','dotnet/docs','dotnet/runtime');" ^
      "$jobs=@();" ^
      "foreach ($r in $repos) {" ^
      "  $name=$r.Split('/')[1]; $dest=Join-Path $auth $name;" ^
      "  if (Test-Path $dest) { Write-Host ('==== [have] '+$name+' ===='); continue };" ^
      "  Write-Host ('==== clone '+$r+' ====');" ^
      "  $jobs += Start-Process -FilePath 'git' -ArgumentList @('clone','--depth','1',('https://github.com/'+$r),$dest) -PassThru -NoNewWindow" ^
      "};" ^
      "if ($jobs.Count -gt 0) { Wait-Process -Id ($jobs.Id); $bad=$jobs | Where-Object { $_.ExitCode -ne 0 }; if ($bad) { exit 1 } };" ^
      "exit 0"
    if errorlevel 1 exit /b 1
    echo ==== AUTHORITY SOURCES READY: !AUTH! ====
    exit /b 0
)

set "PYTHON=python"
set "SCRIPT=%~dp0..\python\download_code_data.py"

if not exist "%SCRIPT%" (
    echo ERROR: %SCRIPT% not found
    exit /b 1
)

set "ARGS=%*"

if not "%LAPLACE_TINY_CODES_DIR%"=="" (
    set "ARGS=%ARGS% --dest %LAPLACE_TINY_CODES_DIR%"
)
if not "%LAPLACE_STACK_V2_DIR%"=="" (
    set "ARGS=%ARGS% --dest %LAPLACE_STACK_V2_DIR%"
)

%PYTHON% "%SCRIPT%" %ARGS%
