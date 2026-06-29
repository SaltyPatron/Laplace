@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "LOG=%LAPLACE_ROOT%\seed-foundation.log"
set "ERR=0"
set "LAPLACE_DEFER_PHYS_INDEX_REBUILD=1"

echo ===== seed-foundation started %DATE% %TIME% =====>> "%LOG%"
echo ===== seed-foundation started %DATE% %TIME% =====

for %%S in (unicode iso639 cili wordnet omw) do (
  echo.>> "%LOG%"
  echo ==== %%S %TIME% ====>> "%LOG%"
  echo ==== %%S ====
  call "%~dp0seed-step.cmd" %%S >> "%LOG%" 2>&1
  if errorlevel 1 (
    echo FAILED: %%S exit=!ERRORLEVEL!>> "%LOG%"
    echo FAILED: %%S
    set "ERR=1"
    goto done
  )
  echo OK: %%S>> "%LOG%"
  echo OK: %%S
)

:done
set "LAPLACE_DEFER_PHYS_INDEX_REBUILD="
if "!ERR!"=="0" (
  call "%~dp0rebuild-phys-indexes.cmd" >> "%LOG%" 2>&1
  if errorlevel 1 set "ERR=1"
)
echo.>> "%LOG%"
echo ===== seed-foundation finished %DATE% %TIME% err=!ERR! =====>> "%LOG%"
echo ===== seed-foundation finished err=!ERR! =====
exit /b !ERR!
