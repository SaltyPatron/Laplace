@echo off
setlocal
rem Idempotent cold redeploy of D:\Data\Laplace\deploy (outside PGDATA).
rem Does NOT touch PGDATA base/ or the laplace database — only the extension deploy tree.
rem Safe when postgresql-x64-18 is stopped. Does not run ALTER SYSTEM (use install-extensions.cmd after PG is up).
call "%~dp0install-extensions.cmd" --files-only
