---
name: 'Test rules'
description: 'Rules for the five xunit test projects and native test state'
applyTo: 'app/**.Tests/**'
---
# Test rules

- Five xunit projects: Core, Substrate, Decomposers, Endpoints.OpenAICompat, Chess.
  Default runs exclude `Tier=perf` (`XUNIT_TIER_EXCLUDE` in test-app.cmd).
- Run via `cmd /c "scripts\win\test-app.cmd [project-substring]"` — env.cmd puts the
  native DLL directories (`build-win\core|dynamics|synthesis`) and PG 18 on PATH; bare
  `dotnet test` from a fresh shell will miss them.
- xunit suites share PROCESS-GLOBAL native state (NativeTestBootstrap.cs wired via
  Directory.Build.targets). Fixtures must NEVER call `CodepointPerfcache.Unload()`.
- Engine gtests: `cmd /c "scripts\win\test-engine.cmd"` (ctest over build-win).
  pg_regress: `cmd /c "scripts\win\regress.cmd"`. Everything: `test-all.cmd`
  (logs to build-win-ext\test-all.log).
- Verify fixes against live data; a passing narrow unit test is not the architectural
  fix (Issue 19 is the canonical example).
- Re-ingest hash identity is the regression test for identity-affecting changes
  (SourceIds, hashing, tier logic).
