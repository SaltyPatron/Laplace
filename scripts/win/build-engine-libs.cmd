@echo off
setlocal
call "%~dp0build-engine.cmd" laplace_core laplace_core_static laplace_dynamics laplace_dynamics_static laplace_synthesis laplace_core_tests
exit /b %ERRORLEVEL%
