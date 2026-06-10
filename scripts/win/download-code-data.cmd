@echo off
setlocal
rem ==== Download code training data for Laplace ingest =======================
rem
rem   download-code-data.cmd tiny-codes
rem     Downloads nampdn-ai/tiny-codes (~400 MB parquet) to D:\Data\Ingest\tiny-codes
rem     Requires HF_TOKEN (user env) and dataset access on huggingface.co
rem
rem   download-code-data.cmd stack-v2 [--langs py,...] [--shards N]
rem     Downloads bigcode/the-stack-v2 language shards (5 per lang by default).
rem     Each shard is ~300-600 MB.  REQUIRES HuggingFace login:
rem       huggingface-cli login
rem     AND acceptance of terms at:
rem       https://huggingface.co/datasets/bigcode/the-stack-v2
rem
rem   download-code-data.cmd authority
rem     Depth-1 clones the official language/platform sources + docs listed in
rem     witness-manifest.json functionality.authority_sources into
rem     D:\Data\Ingest\code-authority. Idempotent: existing clones are skipped.
rem     The seed/e2e repo loops ingest them after local repositories.
rem
rem   Override destination with LAPLACE_TINY_CODES_DIR / LAPLACE_STACK_V2_DIR
rem ==========================================================================
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
