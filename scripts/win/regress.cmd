@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"
set "PGPASSWORD=postgres"
set "PGBIN=C:\Program Files\PostgreSQL\18\bin"
set "PGREGRESS=C:\Program Files\PostgreSQL\18\lib\pgxs\src\test\regress\pg_regress.exe"
set "CONN=-h localhost -U postgres"
set "PATH=%PATH%;C:\Program Files\Git\usr\bin"
if not exist "%LAPLACE_ROOT%\build-win-ext\regress_geom" mkdir "%LAPLACE_ROOT%\build-win-ext\regress_geom"
if not exist "%LAPLACE_ROOT%\build-win-ext\regress_substrate" mkdir "%LAPLACE_ROOT%\build-win-ext\regress_substrate"

"%PGBIN%\dropdb.exe" %CONN% --if-exists laplace_regress_geom || exit /b 1
"%PGBIN%\createdb.exe" %CONN% laplace_regress_geom || exit /b 1
"%PGREGRESS%" --bindir="%PGBIN%" --host=localhost --user=postgres ^
  --inputdir=extension\laplace_geom\tests ^
  --outputdir=build-win-ext\regress_geom ^
  --dbname=laplace_regress_geom --use-existing ^
  hash128 st_4d
set GEOM_RC=%ERRORLEVEL%

"%PGBIN%\dropdb.exe" %CONN% --if-exists laplace_regress_substrate || exit /b 1
"%PGBIN%\createdb.exe" %CONN% laplace_regress_substrate || exit /b 1
"%PGREGRESS%" --bindir="%PGBIN%" --host=localhost --user=postgres ^
  --inputdir=extension\laplace_substrate\tests ^
  --outputdir=build-win-ext\regress_substrate ^
  --dbname=laplace_regress_substrate --use-existing ^
  bootstrap glicko2_aggregate entities_exist_bitmap consensus_signed consensus_period converse identity_law schema_law structural_surface
set SUB_RC=%ERRORLEVEL%

echo geom_rc=%GEOM_RC% substrate_rc=%SUB_RC%
if not "%GEOM_RC%"=="0" exit /b 1
if not "%SUB_RC%"=="0" exit /b 1
