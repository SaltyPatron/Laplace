@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

rem Build PostgreSQL from external/postgresql (the pinned submodule, REL_18_3) into
rem %LAPLACE_PG_PREFIX% — the Windows mirror of the Linux ExternalProject build in
rem external/CMakeLists.txt (${LAPLACE_DEPS_PREFIX}/pgsql-18). PG >= 16 builds on
rem Windows via meson only; compiler is MSVC cl (same policy as build-deps.cmd:
rem plain-C server ABI, engine/extensions stay on icx and link by C ABI).
rem
rem Every meson feature is PINNED, never auto: auto-detection makes the build a
rem function of whatever happens to be installed on the box, which is the opposite
rem of reproducible. The cluster runs --no-locale/UTF8 (bootstrap-laplace-runner.sh)
rem and the substrate does its own UCA ordering natively, so icu stays off; nothing
rem in migrations/seed uses ssl/ldap/xml/PLs on localhost.
rem
rem Knobs (env, documented — no literals buried below):
rem   LAPLACE_PG_BUILD_JOBS  compile parallelism. Defaults to the machine-derived
rem                          P-core budget env.cmd already computes
rem                          (CMAKE_BUILD_PARALLEL_LEVEL). Lower it on hosts with
rem                          thermal/stability constraints.
rem   LAPLACE_PG_BUILDTYPE   meson buildtype; default debugoptimized (-O2 + PDBs).
rem   LAPLACE_PG_CASSERT     true|false; default true. USE_ASSERT_CHECKING also
rem                          enables MEMORY_CONTEXT_CHECKING/CLOBBER_FREED_MEMORY —
rem                          the palloc-overrun catcher. Flip false + --reconfigure
rem                          for a production-hot build.
if not defined LAPLACE_PG_BUILD_JOBS set "LAPLACE_PG_BUILD_JOBS=%CMAKE_BUILD_PARALLEL_LEVEL%"
if not defined LAPLACE_PG_BUILDTYPE set "LAPLACE_PG_BUILDTYPE=debugoptimized"
if not defined LAPLACE_PG_CASSERT set "LAPLACE_PG_CASSERT=true"

rem Pinned build tools (fetched once into %LAPLACE_TOOLS%, hash-verified):
set "WFB_VER=2.5.25"
set "WFB_URL=https://github.com/lexxmark/winflexbison/releases/download/v%WFB_VER%/win_flex_bison-%WFB_VER%.zip"
set "WFB_SHA256=8d324b62be33604b2c45ad1dd34ab93d722534448f55a16ca7292de32b6ac135"
set "WFB_DIR=%LAPLACE_TOOLS%\winflexbison"
set "MESON_VER=1.11.2"

set "RECONF=0"
set "CONFONLY=0"
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--reconfigure"    ( set "RECONF=1"   & shift /1 & goto parse )
if /i "%~1"=="--configure-only" ( set "CONFONLY=1" & shift /1 & goto parse )
echo ERROR: unknown argument %~1 (flags: --reconfigure, --configure-only)
exit /b 1
:parsed

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" acquire build-pg || exit /b 1

rem ---- PG's perl codegen (generate-lwlocknames.pl et al) requires an LF checkout —
rem upstream's documented Windows guidance is core.autocrlf=false. A global
rem autocrlf=true smudges the submodule to CRLF and the parse dies. Detect on a
rem sentinel file; if smudged, pin the SUBMODULE's local config (never the user's
rem global) and re-smudge the tree from the index (HEAD stays at the pin).
perl -e "open my $f, '<:raw', shift or exit 2; local $/; exit((<$f> =~ /\r/) ? 1 : 0)" "%LAPLACE_ROOT%\external\postgresql\src\include\storage\lwlocklist.h"
if errorlevel 1 (
  echo ==== build-pg: CRLF checkout detected — renormalizing external/postgresql to LF ====
  git -C "%LAPLACE_ROOT%\external\postgresql" config core.autocrlf false
  git -C "%LAPLACE_ROOT%\external\postgresql" config core.eol lf
  rem checkout-index --all skips stat-clean files, so it cannot renormalize an
  rem existing checkout. The documented recipe (gitattributes(5)): empty the index,
  rem then reset --hard rewrites every working file through the corrected filters.
  rem Safe here by construction: the submodule is a pristine pinned vendor tree and
  rem the guard only fires when it is CRLF-smudged.
  git -C "%LAPLACE_ROOT%\external\postgresql" rm --cached -r -q .
  git -C "%LAPLACE_ROOT%\external\postgresql" reset --hard -q
  if errorlevel 1 ( echo ERROR: LF renormalization failed & goto fail )
)

rem ---- toolchain: MSVC (same vcvars build-deps.cmd uses), meson, win_flex/win_bison
set "VCVARS=D:\Microsoft Visual Studio\2026\VC\Auxiliary\Build\vcvarsall.bat"
if not exist "%VCVARS%" ( echo ERROR: missing %VCVARS% & goto fail )
call "%VCVARS%" x64 >nul
if errorlevel 1 ( echo ERROR: vcvarsall x64 failed & goto fail )
set "CC=cl"

py -3 -m pip install --user --quiet meson==%MESON_VER%
if errorlevel 1 ( echo ERROR: pip install meson==%MESON_VER% failed & goto fail )
set "MESON=py -3 -m mesonbuild.mesonmain"

if exist "%WFB_DIR%\win_flex.exe" if exist "%WFB_DIR%\win_bison.exe" goto have_wfb
if not exist "%LAPLACE_TOOLS%" mkdir "%LAPLACE_TOOLS%"
echo ==== build-pg: fetching winflexbison %WFB_VER% ====
"%SystemRoot%\System32\curl.exe" -sL -o "%LAPLACE_TOOLS%\winflexbison.zip" "%WFB_URL%"
if errorlevel 1 ( echo ERROR: winflexbison download failed & goto fail )
certutil -hashfile "%LAPLACE_TOOLS%\winflexbison.zip" SHA256 | findstr /i /c:"%WFB_SHA256%" >nul
if errorlevel 1 ( echo ERROR: winflexbison.zip SHA256 mismatch — expected %WFB_SHA256% & goto fail )
if not exist "%WFB_DIR%" mkdir "%WFB_DIR%"
"%SystemRoot%\System32\tar.exe" -xf "%LAPLACE_TOOLS%\winflexbison.zip" -C "%WFB_DIR%"
if errorlevel 1 ( echo ERROR: winflexbison extract failed & goto fail )
:have_wfb

rem ---- configure (idempotent: only when absent or --reconfigure)
set "SETUP_FLAGS="
if "%RECONF%"=="1" set "SETUP_FLAGS=--reconfigure"
if not exist "%LAPLACE_PG_BUILD%\build.ninja" set "RECONF=1"
if "%RECONF%"=="1" (
  echo ==== build-pg: meson setup %LAPLACE_PG_BUILD% ^(%LAPLACE_PG_BUILDTYPE%, cassert=%LAPLACE_PG_CASSERT%^) ====
  %MESON% setup %SETUP_FLAGS% "%LAPLACE_PG_BUILD%" "%LAPLACE_ROOT%\external\postgresql" ^
    --prefix=%LAPLACE_PG_PREFIX:\=/% ^
    --buildtype=%LAPLACE_PG_BUILDTYPE% ^
    -Dcassert=%LAPLACE_PG_CASSERT% ^
    -Dssl=none -Dicu=disabled -Dzlib=disabled -Dlibxml=disabled -Dlz4=disabled ^
    -Dzstd=disabled -Dldap=disabled -Dnls=disabled -Dreadline=disabled ^
    -Dgssapi=disabled -Dpam=disabled -Duuid=none -Dbonjour=disabled ^
    -Dplperl=disabled -Dplpython=disabled -Dpltcl=disabled ^
    -Dtap_tests=disabled -Ddocs=disabled ^
    "-DFLEX=%WFB_DIR:\=/%/win_flex.exe" "-DBISON=%WFB_DIR:\=/%/win_bison.exe"
  if errorlevel 1 ( echo ERROR: meson setup failed & goto fail )
)
if "%CONFONLY%"=="1" goto done

rem ---- compile + install
echo ==== build-pg: compile -j %LAPLACE_PG_BUILD_JOBS% ====
%MESON% compile -C "%LAPLACE_PG_BUILD%" -j %LAPLACE_PG_BUILD_JOBS%
if errorlevel 1 ( echo ERROR: meson compile failed & goto fail )

echo ==== build-pg: install to %LAPLACE_PG_PREFIX% ====
%MESON% install -C "%LAPLACE_PG_BUILD%" --quiet
if errorlevel 1 ( echo ERROR: meson install failed & goto fail )

"%LAPLACE_PG_PREFIX%\bin\pg_config.exe" --version || goto fail

:done
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-pg
exit /b 0

:fail
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tree-lock.ps1" release build-pg
exit /b 1
