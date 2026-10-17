@echo off
rem require-dbname.cmd - fail fast unless the operator named the target database.
rem env.cmd no longer defaults LAPLACE_DBNAME; a script that reads or writes a database
rem calls this first, so an unset name stops it before any connection is made.
if not defined LAPLACE_DBNAME (
  echo ERROR: LAPLACE_DBNAME is not set. Name the target database explicitly, e.g. set LAPLACE_DBNAME=laplace-mono
  exit /b 2
)
exit /b 0