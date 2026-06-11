@echo off
setlocal
rem Build just the native DLLs Laplace.Cli needs (no full gtest sweep) into build-win\.
rem Pure alias for the targeted form of build-engine.cmd (which owns configure + mutex).
rem Agent rules: .github\instructions\build-environment.instructions.md
call "%~dp0build-engine.cmd" laplace_core laplace_dynamics laplace_synthesis laplace_core_tests
exit /b %ERRORLEVEL%
