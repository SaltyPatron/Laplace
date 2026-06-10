@echo off
setlocal
rem Build PG extensions to build-win-ext\ (deploy via install-extensions.cmd, NOT Program Files).
rem Agent rules: .github\instructions\build-environment.instructions.md
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
cmake -B build-win-ext -S extension -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  "-DCMAKE_RC_COMPILER=%LAPLACE_RC%" "-DCMAKE_MT=%LAPLACE_MT%" || exit /b 1
cmake --build build-win-ext || exit /b 1
