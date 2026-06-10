@echo off
setlocal
rem Build native DLLs required by Laplace.Cli (no gtest discovery) into build-win\.
rem Agent rules: .github\instructions\build-environment.instructions.md
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
if not exist build-win\build.ninja (
  call "%~dp0build-engine.cmd" || exit /b 1
)
cmake --build build-win --target laplace_core laplace_dynamics laplace_synthesis laplace_core_tests || exit /b 1
