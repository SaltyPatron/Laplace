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
rem   Override destination with LAPLACE_TINY_CODES_DIR / LAPLACE_STACK_V2_DIR
rem ==========================================================================
call "%~dp0env.cmd"

if "%1"=="" (
    echo Usage: download-code-data.cmd ^<tiny-codes ^| stack-v2^> [options]
    exit /b 1
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
