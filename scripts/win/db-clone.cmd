@echo off
echo ERROR: db-clone.cmd is deprecated.
echo   Isolated tests use fresh db-isolate + prerequisite ingests, not pg clone.
echo   decomposer-test.cmd ^<source^>  — proof in laplace_d_^<source^>
echo   decomposer-promote.cmd ^<source^> — re-ingest into laplace
exit /b 2
