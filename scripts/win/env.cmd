@echo off
set "NoDefaultCurrentDirectoryInExePath="
call "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" >nul 2>&1
set "LAPLACE_ROOT=%~dp0..\.."
set "PATH=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64;%PATH%"
set "PATH=D:\Microsoft Visual Studio\2026\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja;D:\Microsoft Visual Studio\2026\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;%PATH%"
set "PATH=C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin;C:\Program Files (x86)\Intel\oneAPI\mkl\latest\bin;C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin;%PATH%"
set "PATH=%LAPLACE_ROOT%\build-win\core;%LAPLACE_ROOT%\build-win\dynamics;%LAPLACE_ROOT%\build-win\synthesis;%PATH%"
set "LAPLACE_RC=C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/rc.exe"
set "LAPLACE_MT=C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/mt.exe"
