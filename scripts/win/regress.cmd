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

if defined LAPLACE_TEST_SERIAL goto serial_run

echo regress: running geom + substrate suites in parallel
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$root='%LAPLACE_ROOT%'; $pgbin='%PGBIN%'; $pg='%PGREGRESS%'; $ext='%LAPLACE_EXT_BUILD%';" ^
  "$geomOut=Join-Path $ext 'regress_geom'; $subOut=Join-Path $ext 'regress_substrate';" ^
  "$gArgs=@('--bindir='+[char]34+$pgbin+[char]34,'--host=localhost','--user=postgres','--inputdir=extension\laplace_geom\tests','--outputdir='+$geomOut,'--dbname=laplace_regress_geom','--use-existing','hash128','st_4d');" ^
  "$sArgs=@('--bindir='+[char]34+$pgbin+[char]34,'--host=localhost','--user=postgres','--inputdir=extension\laplace_substrate\tests','--outputdir='+$subOut,'--dbname=laplace_regress_substrate','--use-existing','bootstrap','glicko2_aggregate','entities_exist_bitmap','consensus_signed','consensus_upsert','attestation_merge','generation_corpus','converse','word_law','identity_law','schema_law','structural_surface','constituent_edges','walk_richer_forward_pass');" ^
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
  hash128 st_4d
set GEOM_RC=%ERRORLEVEL%

"%PGREGRESS%" --bindir="%PGBIN%" --host=localhost --user=postgres ^
  --inputdir=extension\laplace_substrate\tests ^
  --outputdir=%LAPLACE_EXT_BUILD%\regress_substrate ^
  --dbname=laplace_regress_substrate --use-existing ^
  bootstrap glicko2_aggregate entities_exist_bitmap consensus_signed consensus_upsert attestation_merge generation_corpus converse word_law identity_law schema_law structural_surface constituent_edges walk_richer_forward_pass
set SUB_RC=%ERRORLEVEL%

echo geom_rc=%GEOM_RC% substrate_rc=%SUB_RC%
if not "%GEOM_RC%"=="0" exit /b 1
if not "%SUB_RC%"=="0" exit /b 1
exit /b 0
