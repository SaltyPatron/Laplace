@echo off
setlocal EnableDelayedExpansion

if not defined LAPLACE_ROOT call "%~dp0env.cmd"
if not defined LAPLACE_EMIT_CROSS_LANG set "LAPLACE_EMIT_CROSS_LANG=0"

if not defined LAPLACE_COPY_VALIDATE set "LAPLACE_COPY_VALIDATE=0"
if not defined LAPLACE_SKIP_USAGE set "LAPLACE_SKIP_USAGE=0"
if not defined LAPLACE_SKIP_MODELS set "LAPLACE_SKIP_MODELS=0"
if not defined LAPLACE_LADDER_START set "LAPLACE_LADDER_START=floor"

cd /d "%LAPLACE_ROOT%\app"

if /i "%LAPLACE_LADDER_START%"=="proof" (
  echo ==== ladder start: proof path - floor + knowledge cluster assumed present ====
  echo ==== ladder: build CLI once ====
  dotnet build "%LAPLACE_ROOT%\app\Laplace.Cli\Laplace.Cli.csproj" -c Release -v q --nologo || exit /b 1
  goto stage_proof
)

if "%LAPLACE_LADDER_DRY%"=="1" (
  echo ==== [dry] seed-stage floor ====
  echo ==== [dry] seed-stage document ====
  echo ==== [dry] seed-stage knowledge ====
  if /i not "%LAPLACE_LADDER_STOP%"=="nets" (
    echo ==== [dry] seed-stage usage ====
    if /i not "%LAPLACE_LADDER_STOP%"=="usage" (
      echo ==== [dry] seed-stage code ====
      echo ==== [dry] seed-stage models ====
    )
  )
  goto ladder_done
)

echo ==== ladder: build CLI once ====
dotnet build "%LAPLACE_ROOT%\app\Laplace.Cli\Laplace.Cli.csproj" -c Release -v q --nologo || exit /b 1

call "%~dp0seed-stage.cmd" floor || exit /b 1
call "%~dp0seed-stage.cmd" document || exit /b 1
call "%~dp0seed-stage.cmd" knowledge || exit /b 1
if /i "%LAPLACE_LADDER_STOP%"=="nets" goto ladder_done

:stage_proof
call "%~dp0seed-stage.cmd" usage || exit /b 1
if /i "%LAPLACE_LADDER_STOP%"=="usage" goto ladder_done

call "%~dp0seed-stage.cmd" code || exit /b 1
if /i not "%LAPLACE_SKIP_MODELS%"=="1" (
  call "%~dp0seed-stage.cmd" models || exit /b 1
) else (
  echo ==== models stage skipped ^(LAPLACE_SKIP_MODELS=1^) ====
)

:ladder_done
cd /d "%LAPLACE_ROOT%"
echo ==== LADDER COMPLETE ====
exit /b 0
