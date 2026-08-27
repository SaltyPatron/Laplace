# Legacy repair evidence — 2026-08-27

## Scope and identity

Host: `hart-server`, Intel i7-6850K, 6 physical / 12 logical CPUs,
134,962,335,744 bytes RAM, PostgreSQL 18.3, .NET 10.0.10.

The original remote is `https://github.com/SaltyPatron/Laplace.git`.
The repair starts at main `0e0191b2ee0513b3bb16f837cafbf22fa0a98942`
(PR #1329), on `codex/legacy-drain-repair-20260827` in an isolated checkout.
The repair source is preserved separately at
`/home/ahart/Projects/Laplace-Legacy-Repair-20260827`; the temporary build
checkout remains `/tmp/laplace-legacy-repair-20260827`.

`/home/ahart/Projects/Laplace` is the **refactor** remote, not the original.
The original dated archive is
`/home/ahart/Projects/Laplace-archive-2026-08-24/historical-worktree`; its
README, architecture, and recovery changes were preserved. The clean original
repair checkout `/home/ahart/Projects/Laplace-Live-Repair-20260826` is an older
branch and was not substituted for current main. Neither checkout was edited.
The CI worktree and the separate refactor work were not edited either.

## WordNet drain

Production receipt `45e3bf02-43ca-4349-b647-974b7900faa5`:

- 2026-08-27 17:06:26.653967–17:25:20.372123 UTC; status `ok`.
- 333,972 input units, 86 applied intents, 637,369 entities,
  228,067 physicalities, 2,828,102 persisted attestations.
- Final fold drain: **1,054,113 ms**; fold span: 1,070,696 ms.
- Consensus backend time: 1,262,124 ms across 45 calls (concurrent work can
  exceed wall time); mask backend: 60,163 ms across 24 calls.

`pg_stat_statements` **is installed**, in schema `laplace`. Production
statistics inspected at 18:22 UTC included:

| Query ID | Operation | Maximum execution time |
| --- | --- | ---: |
| 4801102415661347628 | `consensus.upsert_type`, eight arguments | 1,021,057.8 ms |
| 2778317210271173086 | nested prior-state `SELECT ... FOR UPDATE` | 558,548.0 ms |
| 4469554107043751229 | nested consensus `MERGE` | 462,442.4 ms |

Each normalized statement had 462 calls at that observation. Prior reads and
MERGEs accumulated 48,096,185 and 481,005,330 shared-buffer hits respectively,
with zero shared-block reads and zero temporary-block writes in these records.
These are accumulated statistics, not a captured plan for one named ingestion.
Original per-process CPU and peak RSS were not measured by this investigation.

The isolated reproduction creates an empty LIST/HASH relation, analyzes it,
warms a fold plan, then inserts 4,096 cells without another ANALYZE. The old
implementation rescans/materializes the relation under join filters instead of
using the supplied cell keys. Both native fold entry points fail the regression.
This is consistent with the recorded SQL time and the foundation-seed transition;
it is not a full-corpus replay or a projected throughput claim.

The fix keeps the row-locking read correlated with each batch key and gives the
MERGE a fresh parameter-aware plan with materialization disabled. Settings are
function-local and restored afterward. Existing fold parity tests still pass.

## CI, chess, and query failures

[CI run 33095772585](https://github.com/SaltyPatron/Laplace/actions/runs/33095772585)
installed native libraries, then failed managed integration and skipped publish.
The API restarted with August 26 managed assemblies against August 27 native
libraries. The staged attestation stride changed from 152 to 160 bytes in PR
#1329, matching the live `attestation staged batch add failed: -2` failure.

The same PR also left the scalar P/Invoke signature without the new opponent
rating argument. Its existing zero-mask test failed; the corrected signature
now verifies neutral and explicit opponent ratings in the actual COPY output.
The new database column appeared only in `CREATE TABLE IF NOT EXISTS`, so
existing databases missed it. An additive, idempotent upgrade now preserves old
witness fields and supplies the original neutral opponent rating.

Broader testing reproduced a separate SIGSEGV twice. Native frame `rev_cmp`
was running from `codepoint_table_lookup_id`; the managed stack was
`CodepointPerfcache.TryLookupCodepoint` → `EntitiesExistBitmapAsync` → feedback.
Explorer initialization independently reloaded and unmapped the shared cache.
It now uses the common wait-for-initialization path, and reverse-index
initialization finishes under the common gate before lock-free publication.
The first-explorer-use mapping test fails on the original code and passes now.

The operational partition-pressure query also failed with
`round(double precision, integer) does not exist`. Its numeric cast is repaired
and tested by executing the function, not searching its source text.

Deployment now packages matching native libraries with managed applications,
uses application-local native dependencies, and runs the managed Core/ABI gate
before installation. The native-identity test uses the stamped checkout path
instead of silently skipping when build outputs live outside the checkout.

## Verification

Tests ran against a separate PostgreSQL cluster:
`Host=/tmp/laplace-legacy-pg-20260827;Port=55439;Username=ahart;Database=laplace_legacy_regression`.
Fixture database creation also used `PGPORT=55439`. No production fixture
databases were created or dropped.

Build outputs: `/tmp/laplace-legacy-managed-20260827`; native build under the
isolated checkout. Intel oneAPI flags match the production toolchain:
`-O3 -march=haswell -fno-fast-math -ffp-contract=off` for C and C++.
An initial build without the toolchain flags failed exact floating-point SQL
parity; that configuration was corrected before final verification/publishing.

Commands, with the isolated `LAPLACE_DB`, `PGPORT`, `LAPLACE_BUILD_ROOT`, and
`LAPLACE_PERFCACHE_BIN` exported:

```sh
dotnet test app/Laplace.Core.Tests/Laplace.Core.Tests.csproj -c Release
dotnet test app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj -c Release
dotnet test app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj -c Release
PGHOST=/tmp/laplace-legacy-pg-20260827 PGPORT=55439 PGUSER=laplace_admin \
  ctest --test-dir build -L regress --output-on-failure -j 2
python3 scripts/validate-pipeline.py
bash scripts/test-deploy-payload-sync.sh
```

Results: Core **194 passed**; Substrate **801 passed, one skipped**; Endpoints
**196 passed, none skipped**; all **six** PostgreSQL CTest targets passed,
including all 26 substrate SQL scripts. The Substrate skip is an existing probe
with a hard-coded Windows WordNet path, not a claimed pass. The isolated cluster
has the actual perfcaches and application migrations; endpoint conversation tests
ran with a real, capped Unicode seed, not a fabricated completion marker.

Deliberate regressions: old fold plans fail both entry-point plan tests; removing
the additive column migration makes its preservation test fail with SQLSTATE
42703; the old partition-pressure query fails with SQLSTATE 42883; the old
explorer initialization fails mapping identity and crashes concurrent endpoint
tests. Restoring the repairs makes these checks pass.

## Live state and remaining activation

The final API payload was installed and the legacy API restarted at
**2026-08-27 18:34:27 UTC**. The SPA, logs, corpus, and unrelated configuration
were preserved. The sole environment-content change prepends `/opt/laplace/app`
to `LD_LIBRARY_PATH`; its replacement file remains private to ahart and the
laplace-runner group, with group-write permission for subsequent CI updates.

Live checks: opening legal moves = 20; unrecorded play session starts; learned
PST = 384 squares; explorer decomposition of `dog` = 9 nodes; `dog` describe
query returns successfully. These prove route recovery, not corpus semantic
quality or full product acceptance.

Recovery copies are in
`/home/ahart/Projects/laplace-legacy-runtime-backup-20260827.UqXlcr` (mode 0700).
`app/` is the original payload; `app-first-repair/` is the first repaired
payload. `pending-postgres/` contains the tested native/SQL extension update;
its RPATH was packaged for `/opt/laplace`, not the temporary test prefix.

**The live PostgreSQL extension has not been updated or restarted.** Activating
the drain and diagnostic-query fixes requires the shared-service restart
decision. No main-branch push, PR merge, or deploying CI dispatch was performed.

The final readiness check around 18:41 UTC found an active document ingest
(`ingest document /vault/Data/test-data`, PID 525290), a live physicality COPY,
and a refactor CI worker. Do not restart the shared PostgreSQL service during
that work; recheck clients and ingestion before any approved activation.
Temporary API sidecars and the isolated PostgreSQL test server were stopped;
their files and the production API were preserved.

One CLI `ingest unicode --help` probe was interpreted as an ingest command by
the legacy parser. The production completion marker short-circuited it: zero
intents, entities, physicalities, or consensus cells were ingested; the CLI
also attempted its standard canonical-name registration. This was reported
immediately. Subsequent CLI/test invocations explicitly targeted the isolated
database.
