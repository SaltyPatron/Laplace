@echo off
setlocal
rem Regenerate docs/INVENTORY.md (write-if-changed). Pass --check to verify only.
python "%~dp0..\docs-inventory.py" %*
exit /b %ERRORLEVEL%
