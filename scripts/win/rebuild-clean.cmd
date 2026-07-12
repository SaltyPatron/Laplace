@echo off
rem rebuild-clean.cmd — wipe engine/ext/app build trees (explicit; not the rebuild default).
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-win-ext || exit /b 1

if exist "%LAPLACE_ENGINE_BUILD%" (
  echo rebuild-clean: removing %LAPLACE_ENGINE_BUILD% ...
  rmdir /s /q "%LAPLACE_ENGINE_BUILD%"
)
if exist "%LAPLACE_EXT_BUILD%" (
  echo rebuild-clean: removing %LAPLACE_EXT_BUILD% ...
  rmdir /s /q "%LAPLACE_EXT_BUILD%"
)
if exist "%LAPLACE_BUILD_ROOT%\app" (
  echo rebuild-clean: removing %LAPLACE_BUILD_ROOT%\app ...
  rmdir /s /q "%LAPLACE_BUILD_ROOT%\app"
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-win-ext
powershell -NoProfile -ExecutionPolicy Bypass -Command ". '%~dp0laplace-paths.ps1'; Remove-StaleInRepoBuildArtifacts"
echo rebuild-clean: done
exit /b 0
