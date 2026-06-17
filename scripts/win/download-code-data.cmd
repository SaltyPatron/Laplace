@echo off
setlocal
call "%~dp0env.cmd"

if "%1"=="" (
    echo Usage: download-code-data.cmd ^<tiny-codes ^| stack-v2 ^| authority^> [options]
    exit /b 1
)

if /i "%1"=="authority" (
    setlocal EnableDelayedExpansion
    set "AUTH=D:\Data\Ingest\code-authority"
    if not exist "!AUTH!" mkdir "!AUTH!"
    for %%R in ("postgres/postgres" "python/cpython" "dotnet/docs" "dotnet/runtime") do (
        for /f "tokens=2 delims=/" %%N in ("%%~R") do (
            if not exist "!AUTH!\%%N" (
                echo ==== clone %%~R ====
                git clone --depth 1 "https://github.com/%%~R" "!AUTH!\%%N" || exit /b 1
            ) else (
                echo ==== [have] %%N ====
            )
        )
    )
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
