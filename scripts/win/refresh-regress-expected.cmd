@echo off
setlocal
call "%~dp0env.cmd"
cd /d "%LAPLACE_ROOT%"

set "SUB_RESULTS=%LAPLACE_ROOT%\build-win-ext\regress_substrate\results"
set "SUB_EXPECTED=%LAPLACE_ROOT%\extension\laplace_substrate\tests\expected"
set "GEO_RESULTS=%LAPLACE_ROOT%\build-win-ext\regress_geom\results"
set "GEO_EXPECTED=%LAPLACE_ROOT%\extension\laplace_geom\tests\expected"

if not exist "%SUB_RESULTS%" (
  echo run scripts\win\regress.cmd first — no %SUB_RESULTS%
  exit /b 1
)

for %%T in (bootstrap glicko2_aggregate entities_exist_bitmap consensus_signed consensus_period consensus_fold generation_corpus converse word_law identity_law schema_law structural_surface) do (
  if not exist "%SUB_RESULTS%\%%T.out" (
    echo missing %SUB_RESULTS%\%%T.out — regress did not produce %%T
    exit /b 1
  )
  copy /y "%SUB_RESULTS%\%%T.out" "%SUB_EXPECTED%\%%T.out" >nul || exit /b 1
  echo updated substrate expected\%%T.out
)

if exist "%GEO_RESULTS%" (
  for %%T in (hash128 st_4d) do (
    if exist "%GEO_RESULTS%\%%T.out" (
      copy /y "%GEO_RESULTS%\%%T.out" "%GEO_EXPECTED%\%%T.out" >nul || exit /b 1
      echo updated geom expected\%%T.out
    )
  )
)

echo refresh-regress-expected: copied pg_regress results into tests/expected — commit those files after review
exit /b 0
