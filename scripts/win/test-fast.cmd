@echo off
setlocal
rem ============================================================================
rem  test-fast.cmd — the sub-minute unit gate, honestly named.
rem
rem  Runs ONLY unit-shaped tests: Tier!=perf AND Tier!=db (the 12 PG-bound
rem  integration classes are tagged Tier=db), and skips the chess engine suite
rem  (no DB, but ~11.5 min of search execution — functionality, not a unit gate).
rem
rem  What this does NOT gate: Postgres integration (create/drop test DBs, COPY
rem  spine, fold parity), seed integration, chess search, perf tiers, pg_regress,
rem  verify-fk. Those are FUNCTIONALITY runs and live in test-all.cmd — run them
rem  before merging anything that touches the write spine, extension SQL, or
rem  chess. This gate is the pre-commit signal, not the release gate.
rem ============================================================================
set "XUNIT_TIER_EXCLUDE=Tier!=perf&Tier!=db"
for %%P in (Laplace.Substrate.Tests Laplace.Decomposers.Tests Laplace.Endpoints.OpenAICompat.Tests) do (
  call "%~dp0test-app.cmd" %%P || (
    echo ==== test-fast: FAILED in %%P ====
    exit /b 1
  )
)
echo ==== test-fast: all unit tiers green ====
exit /b 0
