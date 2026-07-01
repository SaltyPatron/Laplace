@echo off



setlocal
call "%~dp0env.cmd"

set "QT_VER=6.8.3"
set "QT_ARCH=win64_msvc2022_64"
set "QT_ROOT=D:\Qt"
set "QT_DIR=%QT_ROOT%\%QT_VER%\msvc2022_64"
set "CC_SRC=%LAPLACE_ROOT%\external\cutechess"
set "CC_BUILD=%LAPLACE_ROOT%\build-cutechess"
set "VCVARS=D:\Microsoft Visual Studio\2026\VC\Auxiliary\Build\vcvars64.bat"

if not exist "%CC_SRC%\CMakeLists.txt" (
  echo [build-cutechess] submodule missing — run: git submodule update --init external\cutechess
  exit /b 1
)


if not exist "%QT_DIR%\lib\cmake\Qt6\Qt6Config.cmake" (
  echo ==== installing Qt %QT_VER% %QT_ARCH% -^> %QT_ROOT% ====
  python -m pip install --user --quiet aqtinstall || exit /b 1
  python -m aqt install-qt windows desktop %QT_VER% %QT_ARCH% -O "%QT_ROOT%" -m qt5compat || exit /b 1
) else (
  echo ==== Qt present: %QT_DIR% ====
)


call "%VCVARS%" || exit /b 1


echo ==== configuring cutechess (cli) ====
cmake -S "%CC_SRC%" -B "%CC_BUILD%" -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release -DWITH_TESTS=OFF ^
  -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl ^
  -DCMAKE_PREFIX_PATH="%QT_DIR%" || exit /b 1


echo ==== building cutechess-cli ====
cmake --build "%CC_BUILD%" --target cli || exit /b 1

echo.
echo [build-cutechess] OK -^> %CC_BUILD%\cutechess-cli.exe
exit /b 0
