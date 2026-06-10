@echo off
setlocal
rem Build engine to build-win\ (NOT cmake --install). Agent rules: .github\instructions\build-environment.instructions.md
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
cmake -B build-win -S engine -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx ^
  "-DCMAKE_RC_COMPILER=%LAPLACE_RC%" "-DCMAKE_MT=%LAPLACE_MT%" ^
  -DCMAKE_WINDOWS_EXPORT_ALL_SYMBOLS=ON ^
  -DBLAKE3_SIMD_TYPE=none ^
  -DBUILD_TESTING=ON ^
  "-DLAPLACE_UCD_PATH=D:/Data/Ingest/Unicode/Public/17.0.0" ^
  "-DLAPLACE_UCDXML_ZIP=D:/Data/Ingest/Unicode/Public/17.0.0/ucdxml/ucd.nounihan.flat.zip" ^
  "-DLAPLACE_DUCET_FILE=D:/Data/Ingest/Unicode/Public/17.0.0/uca/allkeys.txt" ^
  "-DLAPLACE_UCD_CONFORMANCE_DIR=D:/Data/Ingest/Unicode/Public/17.0.0/ucd" ^
  "-DLIBXML2_INCLUDE_DIR=C:/Program Files/PostgreSQL/18/include" ^
  "-DLIBXML2_LIBRARY=C:/Program Files/PostgreSQL/18/lib/libxml2.lib" || exit /b 1
cmake --build build-win || exit /b 1
