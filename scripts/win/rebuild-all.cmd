@echo off
setlocal EnableDelayedExpansion

rem rebuild-all.cmd — thin module runner. Stages live in their own scripts.
rem
rem   rebuild-all.cmd [--clean] [module|preset ...]
rem
rem Presets:
rem   native   codegen deps engine extensions perfcheck
rem   default  native + install + app          (no IIS)
rem   ship     default + publish               (publish-deploy → IIS)
rem   all      alias for ship
rem
rem Modules (any order; run in fixed pipeline order):
rem   clean codegen deps engine extensions perfcheck install app publish
rem
rem Compat flags:
rem   --clean          prepend clean module
rem   --skip-app       drop app from the selection
rem   --skip-install   drop install + publish from the selection
rem   --skip-clean     obsolete no-op
rem
rem No modules/presets → default.

call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "WANT_CLEAN=0"
set "WANT_CODEGEN=0"
set "WANT_DEPS=0"
set "WANT_ENGINE=0"
set "WANT_EXT=0"
set "WANT_PERF=0"
set "WANT_INSTALL=0"
set "WANT_APP=0"
set "WANT_PUBLISH=0"
set "HAVE_SELECTION=0"
set "SKIP_APP=0"
set "SKIP_INSTALL=0"
set "APP_SKIP_CLEAN=0"

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--clean" (
  set "WANT_CLEAN=1"
  set "HAVE_SELECTION=1"
  shift /1 & goto parse
)
if /i "%~1"=="--skip-clean" (
  echo rebuild-all: --skip-clean is obsolete ^(incremental is the default^); ignoring
  shift /1 & goto parse
)
if /i "%~1"=="--skip-app" (
  set "SKIP_APP=1"
  shift /1 & goto parse
)
if /i "%~1"=="--skip-install" (
  set "SKIP_INSTALL=1"
  shift /1 & goto parse
)
if /i "%~1"=="--help" goto usage
if /i "%~1"=="-h" goto usage
if /i "%~1"=="clean"       ( set "WANT_CLEAN=1"      & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="codegen"     ( set "WANT_CODEGEN=1"    & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="deps"        ( set "WANT_DEPS=1"       & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="engine"      ( set "WANT_ENGINE=1"     & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="extensions"  ( set "WANT_EXT=1"        & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="ext"         ( set "WANT_EXT=1"        & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="perfcheck"   ( set "WANT_PERF=1"       & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="install"     ( set "WANT_INSTALL=1"    & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="app"         ( set "WANT_APP=1"        & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="publish"     ( set "WANT_PUBLISH=1"    & set "HAVE_SELECTION=1" & shift /1 & goto parse )
if /i "%~1"=="native" (
  set "WANT_CODEGEN=1" & set "WANT_DEPS=1" & set "WANT_ENGINE=1" & set "WANT_EXT=1" & set "WANT_PERF=1"
  set "HAVE_SELECTION=1" & shift /1 & goto parse
)
if /i "%~1"=="default" (
  call :preset_default
  set "HAVE_SELECTION=1" & shift /1 & goto parse
)
if /i "%~1"=="ship" (
  call :preset_default
  set "WANT_PUBLISH=1"
  set "HAVE_SELECTION=1" & shift /1 & goto parse
)
if /i "%~1"=="all" (
  call :preset_default
  set "WANT_PUBLISH=1"
  set "HAVE_SELECTION=1" & shift /1 & goto parse
)
echo rebuild-all: unknown argument %~1
echo.
goto usage

:parsed
if "%HAVE_SELECTION%"=="0" call :preset_default

if "%SKIP_APP%"=="1" set "WANT_APP=0"
if "%SKIP_INSTALL%"=="1" (
  set "WANT_INSTALL=0"
  set "WANT_PUBLISH=0"
)

echo ===== rebuild-all modules =====
if "%WANT_CLEAN%"=="1"     echo   clean
if "%WANT_CODEGEN%"=="1"   echo   codegen
if "%WANT_DEPS%"=="1"      echo   deps
if "%WANT_ENGINE%"=="1"    echo   engine
if "%WANT_EXT%"=="1"       echo   extensions
if "%WANT_PERF%"=="1"      echo   perfcheck
if "%WANT_INSTALL%"=="1"   echo   install
if "%WANT_APP%"=="1"       echo   app
if "%WANT_PUBLISH%"=="1"   echo   publish
echo.

if "%WANT_CLEAN%"=="1" (
  call "%~dp0rebuild-clean.cmd" || exit /b 1
  set "APP_SKIP_CLEAN=1"
)
if "%WANT_CODEGEN%"=="1" (
  echo ===== codegen =====
  powershell -NoProfile -ExecutionPolicy Bypass -File "%LAPLACE_ROOT%\scripts\codegen-attestation-law.ps1" || exit /b 1
)
if "%WANT_DEPS%"=="1" (
  echo ===== deps =====
  if exist "%LAPLACE_DEPS_PREFIX%\geos\include\geos_c.h" if exist "%LAPLACE_DEPS_PREFIX%\proj\include\proj.h" if exist "%LAPLACE_DEPS_PREFIX%\gdal\include\gdal.h" (
    echo deps already present under %LAPLACE_DEPS_PREFIX% — skipping build-deps
  ) else (
    call "%~dp0build-deps.cmd" || exit /b 1
  )
)
if "%WANT_ENGINE%"=="1" (
  echo ===== engine =====
  call "%~dp0build-engine.cmd" || exit /b 1
)
if "%WANT_EXT%"=="1" (
  echo ===== extensions =====
  if "%WANT_CODEGEN%"=="1" (
    rem codegen module already ran — avoid double invoke.
    call "%~dp0build-extensions.cmd" --skip-codegen || exit /b 1
  ) else (
    call "%~dp0build-extensions.cmd" || exit /b 1
  )
)
if "%WANT_PERF%"=="1" (
  echo ===== perfcheck =====
  call "%~dp0rebuild-perfcheck.cmd" || exit /b 1
)
if "%WANT_INSTALL%"=="1" (
  echo ===== install =====
  call "%~dp0install-extensions.cmd" --skip-build || exit /b 1
)
if "%WANT_APP%"=="1" (
  echo ===== app =====
  if "%APP_SKIP_CLEAN%"=="1" (
    call "%~dp0build-app.cmd" --skip-clean || exit /b 1
  ) else (
    call "%~dp0build-app.cmd" || exit /b 1
  )
)
if "%WANT_PUBLISH%"=="1" (
  echo ===== publish =====
  call "%~dp0publish-deploy.cmd" --skip-managed-build || exit /b 1
)

echo ===== rebuild-all COMPLETE =====
exit /b 0

:preset_default
set "WANT_CODEGEN=1"
set "WANT_DEPS=1"
set "WANT_ENGINE=1"
set "WANT_EXT=1"
set "WANT_PERF=1"
set "WANT_INSTALL=1"
set "WANT_APP=1"
exit /b 0

:usage
echo Usage: rebuild-all.cmd [--clean] [--skip-app] [--skip-install] [module^|preset ...]
echo.
echo Presets:  native ^| default ^| ship ^| all
echo Modules:  clean codegen deps engine extensions perfcheck install app publish
echo.
echo Default ^(no args^): codegen deps engine extensions perfcheck install app
echo   ^(publish/IIS is opt-in via: ship ^| all ^| publish^)
exit /b 2
