@echo off
rem Generic Laplace.Cli runner: enters through env.cmd (oneAPI MKL + build-win on PATH,
rem harness env cleaned) then invokes the engine CLI with all args forwarded.
rem   scripts\win\cli.cmd synthesize substrate --native-vocab 32000 --dim 2048 out.gguf
setlocal
call "%~dp0env.cmd"
"%LAPLACE_ROOT%\app\Laplace.Cli\bin\Release\net10.0\Laplace.Cli.exe" %*
exit /b %ERRORLEVEL%
