@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"

if not defined LAPLACE_CHESS_TEST_DB set "LAPLACE_CHESS_TEST_DB=laplace_chess_test"

set "SCRIPTS=%~dp0"
set "MODE=floor"
set "RESET=0"
set "PGN="

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--reset" (set "RESET=1" & shift & goto parse_args)
if /i "%~1"=="floor" (set "MODE=floor" & shift & goto parse_args)
if /i "%~1"=="foundation" (set "MODE=foundation" & shift & goto parse_args)
if /i "%~1"=="openings" (set "MODE=openings" & shift & goto parse_args)
if /i "%~1"=="pilot" (set "MODE=pilot" & shift & goto parse_args)
if /i "%~1"=="full" (set "MODE=full" & shift & goto parse_args)
if not defined PGN (set "PGN=%~1" & shift & goto parse_args)
echo ERROR: unknown argument %~1
exit /b 2

:args_done
set "LAPLACE_DBNAME=%LAPLACE_CHESS_TEST_DB%"
set "LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=%LAPLACE_CHESS_TEST_DB%;Command Timeout=0"
set "LAPLACE_CHESS_DB=%LAPLACE_DB%"

if "%RESET%"=="1" (
  echo ==== reset test DB %LAPLACE_CHESS_TEST_DB% ====
  call "%SCRIPTS%install-extensions.cmd" --recycle || exit /b 1
  call "%SCRIPTS%db-isolate.cmd" "%LAPLACE_CHESS_TEST_DB%" || exit /b 1
) else (
  "%PGBIN%\psql.exe" -h localhost -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='%LAPLACE_CHESS_TEST_DB%'" 2>nul | findstr /R "^1$" >nul
  if errorlevel 1 (
    echo ==== create test DB %LAPLACE_CHESS_TEST_DB% ====
    call "%SCRIPTS%install-extensions.cmd" --recycle || exit /b 1
    call "%SCRIPTS%db-isolate.cmd" "%LAPLACE_CHESS_TEST_DB%" || exit /b 1
  ) else (
    echo ==== using existing test DB %LAPLACE_CHESS_TEST_DB% (pass --reset to recreate) ====
  )
)

if /i "%MODE%"=="foundation" goto foundation

echo ==== floor: unicode ====
call "%SCRIPTS%seed-step.cmd" unicode || exit /b 1

echo ==== floor: iso639 ====
call "%SCRIPTS%seed-step.cmd" iso639 || exit /b 1

if /i "%MODE%"=="floor" goto done

:foundation
echo ==== foundation: witness-manifest batch via seed-foundation.cmd ====
call "%SCRIPTS%seed-foundation.cmd" || exit /b 1
if /i "%MODE%"=="foundation" goto stats

set "OPENINGS=%INGEST%\Games\Chess\openings"
if not exist "%OPENINGS%" (
  echo ERROR: openings path missing: %OPENINGS%
  exit /b 1
)

echo ==== chess openings book ====
call "%SCRIPTS%seed-step.cmd" openings "%OPENINGS%" || exit /b 1

if /i "%MODE%"=="openings" goto done

set "LUMBRAS=%INGEST%\Games\Chess\Lumbras"
if not exist "%LUMBRAS%" (
  echo ERROR: Lumbras path missing: %LUMBRAS%
  exit /b 1
)

if /i "%MODE%"=="pilot" goto pilot
if /i "%MODE%"=="full" goto full
goto done

:pilot
if not defined PGN (
  for /f "delims=" %%f in ('dir /b /o:n "%LUMBRAS%\*.pgn" 2^>nul') do (
    set "PGN=%LUMBRAS%\%%f"
    goto pilot_have
  )
  echo ERROR: no .pgn files under %LUMBRAS%
  exit /b 1
)
:pilot_have
echo ==== pilot chess ingest: !PGN! ====
call "%SCRIPTS%seed-step.cmd" chess "!PGN!" || exit /b 1
goto stats

:full
echo ==== full Lumbras chess ingest ====
call "%SCRIPTS%seed-step.cmd" chess "%LUMBRAS%" || exit /b 1
goto stats

:stats
echo ==== post-ingest counts on %LAPLACE_CHESS_TEST_DB% ====
"%PGBIN%\psql.exe" -h localhost -U postgres -d %LAPLACE_CHESS_TEST_DB% -P pager=off -v ON_ERROR_STOP=1 -c "SET search_path = laplace, public; SELECT 'entities' AS k, count(*)::bigint FROM entities UNION ALL SELECT 'consensus', count(*)::bigint FROM consensus UNION ALL SELECT 'attestations', count(*)::bigint FROM attestations ORDER BY 1;"

:done
echo ==== chess-platform-seed complete (%MODE%) on %LAPLACE_CHESS_TEST_DB% ====
exit /b 0