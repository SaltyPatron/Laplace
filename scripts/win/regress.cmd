@echo off
setlocal EnableDelayedExpansion
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
rem Derive from PGBIN (env.cmd owns the toolchain path) so custom/self-hosted PG
rem installs work by changing ONE place, not this hardcoded literal.
set "PGREGRESS=%PGBIN%\..\lib\pgxs\src\test\regress\pg_regress.exe"
set "CONN=-h localhost -U postgres"
set "PATH=%PATH%;C:\Program Files\Git\usr\bin"
if not exist "%LAPLACE_EXT_BUILD%\regress_geom" mkdir "%LAPLACE_EXT_BUILD%\regress_geom"
if not exist "%LAPLACE_EXT_BUILD%\regress_substrate" mkdir "%LAPLACE_EXT_BUILD%\regress_substrate"

"%PGBIN%\dropdb.exe" %CONN% --force --if-exists laplace_regress_geom || exit /b 1
"%PGBIN%\createdb.exe" %CONN% laplace_regress_geom || exit /b 1
"%PGBIN%\dropdb.exe" %CONN% --force --if-exists laplace_regress_substrate || exit /b 1
"%PGBIN%\createdb.exe" %CONN% laplace_regress_substrate || exit /b 1

rem SINGLE SOURCE OF TRUTH for the suite lists: tests\CMakeLists.txt REGRESS_TESTS.
rem These lists used to be duplicated here by hand and silently drifted — chat_loop
rem was registered for ctest but never ran on Windows. Parse, never restate.
rem NOTE: no '|' and no '^' inside these backticked commands — cmd's parser eats
rem both before PowerShell ever sees them. Filter with (-ne ''), match with (.*?).
for /f "usebackq delims=" %%T in (`powershell -NoProfile -Command "$m=[regex]::Match((Get-Content -Raw 'extension\laplace_geom\tests\CMakeLists.txt'),'set\(REGRESS_TESTS\s+(.*?)\)'); if(-not $m.Success){exit 1}; $t=$m.Groups[1].Value -split '\s+'; ($t -ne '') -join ' '"`) do set "GEOM_TESTS=%%T"
for /f "usebackq delims=" %%T in (`powershell -NoProfile -Command "$m=[regex]::Match((Get-Content -Raw 'extension\laplace_substrate\tests\CMakeLists.txt'),'set\(REGRESS_TESTS\s+(.*?)\)'); if(-not $m.Success){exit 1}; $t=$m.Groups[1].Value -split '\s+'; ($t -ne '') -join ' '"`) do set "SUB_TESTS=%%T"
if not defined GEOM_TESTS echo regress: could not parse REGRESS_TESTS from extension\laplace_geom\tests\CMakeLists.txt & exit /b 1
if not defined SUB_TESTS echo regress: could not parse REGRESS_TESTS from extension\laplace_substrate\tests\CMakeLists.txt & exit /b 1
echo regress: geom      = %GEOM_TESTS%
echo regress: substrate = %SUB_TESTS%

if defined LAPLACE_TEST_SERIAL goto serial_run

echo regress: running geom + substrate suites in parallel
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$root='%LAPLACE_ROOT%'; $pgbin='%PGBIN%'; $pg='%PGREGRESS%'; $ext='%LAPLACE_EXT_BUILD%';" ^
  "$geomOut=Join-Path $ext 'regress_geom'; $subOut=Join-Path $ext 'regress_substrate';" ^
  "$gT='%GEOM_TESTS%' -split ' '; $gT=$gT -ne ''; $sT='%SUB_TESTS%' -split ' '; $sT=$sT -ne '';" ^
  "$gArgs=@('--bindir='+[char]34+$pgbin+[char]34,'--host=localhost','--user=postgres','--inputdir=extension\laplace_geom\tests','--outputdir='+$geomOut,'--dbname=laplace_regress_geom','--use-existing') + $gT;" ^
  "$sArgs=@('--bindir='+[char]34+$pgbin+[char]34,'--host=localhost','--user=postgres','--inputdir=extension\laplace_substrate\tests','--outputdir='+$subOut,'--dbname=laplace_regress_substrate','--use-existing') + $sT;" ^
  "$g=Start-Process -FilePath $pg -ArgumentList $gArgs -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $geomOut 'parallel.out') -RedirectStandardError (Join-Path $geomOut 'parallel.err');" ^
  "$s=Start-Process -FilePath $pg -ArgumentList $sArgs -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $subOut 'parallel.out') -RedirectStandardError (Join-Path $subOut 'parallel.err');" ^
  "$null=$g.Handle; $null=$s.Handle; $g.WaitForExit(); $s.WaitForExit();" ^
  "foreach ($f in @((Join-Path $geomOut 'parallel.out'),(Join-Path $geomOut 'parallel.err'),(Join-Path $subOut 'parallel.out'),(Join-Path $subOut 'parallel.err'))) { if (Test-Path $f) { Get-Content $f } };" ^
  "if ($g.ExitCode -ne 0 -or $s.ExitCode -ne 0) { Write-Host ('geom_rc='+$g.ExitCode+' substrate_rc='+$s.ExitCode); exit 1 };" ^
  "Write-Host ('geom_rc='+$g.ExitCode+' substrate_rc='+$s.ExitCode); exit 0"
exit /b %ERRORLEVEL%

:serial_run
echo regress: serial mode (LAPLACE_TEST_SERIAL)
"%PGREGRESS%" --bindir="%PGBIN%" --host=localhost --user=postgres ^
  --inputdir=extension\laplace_geom\tests ^
  --outputdir=%LAPLACE_EXT_BUILD%\regress_geom ^
  --dbname=laplace_regress_geom --use-existing ^
  %GEOM_TESTS%
set GEOM_RC=%ERRORLEVEL%

"%PGREGRESS%" --bindir="%PGBIN%" --host=localhost --user=postgres ^
  --inputdir=extension\laplace_substrate\tests ^
  --outputdir=%LAPLACE_EXT_BUILD%\regress_substrate ^
  --dbname=laplace_regress_substrate --use-existing ^
  %SUB_TESTS%
set SUB_RC=%ERRORLEVEL%

echo geom_rc=%GEOM_RC% substrate_rc=%SUB_RC%
if not "%GEOM_RC%"=="0" exit /b 1
if not "%SUB_RC%"=="0" exit /b 1
exit /b 0
