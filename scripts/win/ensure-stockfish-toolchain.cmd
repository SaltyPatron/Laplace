@echo off
rem No setlocal: the selected source compiler and GNU tools feed the caller.
call "%~dp0env.cmd" || exit /b 1
set "LAPLACE_STOCKFISH_TOOLCHAIN_ENV=%LAPLACE_BUILD_ROOT%\stockfish-toolchain.env"
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0ensure-stockfish-toolchain.ps1" -EnvironmentFile "%LAPLACE_STOCKFISH_TOOLCHAIN_ENV%"
if errorlevel 1 exit /b 1
for /f "usebackq tokens=1,* delims==" %%A in ("%LAPLACE_STOCKFISH_TOOLCHAIN_ENV%") do set "%%A=%%B"
if defined LAPLACE_STOCKFISH_TOOLCHAIN_PATH set "PATH=%LAPLACE_STOCKFISH_TOOLCHAIN_PATH%;%PATH%"
exit /b 0
