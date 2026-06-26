## Bucket: S2_scripts_cmd_ps1

Windows batch/PowerShell automation + `decomposer-gates.json` + two small text/json fixtures.

### Files read (58/58 — all read in full)
- [x] scripts/codegen-attestation-law.ps1
- [x] scripts/decomposer-gates.json
- [x] scripts/prompts_smoke.txt (5-line prompt fixture; clean)
- [x] scripts/win/build-engine-asan.cmd
- [x] scripts/win/build-engine-libs.cmd
- [x] scripts/win/build-engine.cmd
- [x] scripts/win/build-extensions.cmd
- [x] scripts/win/build-web.cmd
- [x] scripts/win/cli.cmd (clean — thin CLI launcher)
- [x] scripts/win/conceptnet-bench.ps1
- [x] scripts/win/converse.cmd
- [x] scripts/win/db-clone.cmd (intentional deprecation tombstone — exits 2)
- [x] scripts/win/db-isolate.cmd
- [x] scripts/win/db-reset.cmd
- [x] scripts/win/decomposer-matrix.cmd
- [x] scripts/win/decomposer-promote.cmd
- [x] scripts/win/decomposer-smoke.cmd
- [x] scripts/win/decomposer-test.cmd
- [x] scripts/win/download-code-data.cmd
- [x] scripts/win/e2e-master.cmd
- [x] scripts/win/e2e-web.cmd
- [x] scripts/win/e2e.cmd
- [x] scripts/win/ensure-floor.cmd
- [x] scripts/win/env.cmd
- [x] scripts/win/gen-sql.ps1
- [x] scripts/win/index-content.cmd
- [x] scripts/win/ingest-repo.cmd
- [x] scripts/win/ingest-text.cmd
- [x] scripts/win/install-extensions.cmd
- [x] scripts/win/locks.cmd
- [x] scripts/win/locks.ps1
- [x] scripts/win/omw-bench.ps1
- [x] scripts/win/publish.cmd
- [x] scripts/win/rebuild-all.cmd
- [x] scripts/win/refresh-substrate-module.cmd
- [x] scripts/win/regress.cmd
- [x] scripts/win/run-decomposer-matrix-logged.ps1
- [x] scripts/win/seed-continue.cmd
- [x] scripts/win/seed-deferred-lexical.cmd
- [x] scripts/win/seed-full.cmd
- [x] scripts/win/seed-ladder.cmd
- [x] scripts/win/seed-layer-check.ps1
- [x] scripts/win/seed-resume-prove.cmd
- [x] scripts/win/seed-stage.cmd
- [x] scripts/win/seed-step.cmd
- [x] scripts/win/seed-substrate.cmd
- [x] scripts/win/serve.cmd
- [x] scripts/win/status.cmd (clean launcher)
- [x] scripts/win/status.ps1
- [x] scripts/win/test-all.cmd
- [x] scripts/win/test-app.cmd
- [x] scripts/win/test-engine.cmd
- [x] scripts/win/tree-lock.ps1 (clean — directory-based mutex with stale-PID reclaim)
- [x] scripts/win/tune-pg.cmd
- [x] scripts/win/verify-deploy.cmd
- [x] scripts/win/wait-for-pg.ps1
- [x] scripts/win/walk-verdict.cmd
- [x] scripts/win/witness-manifest.json

Cross-checked outside the bucket to verify claims: `app/Laplace.Cli/IngestCommands.cs`,
`app/Laplace.Ingestion/IngestRunner.cs`, `app/Laplace.Engine.Core/IntentStage.cs`,
`scripts/decomposer-gate-check.py` (the gate evaluator that consumes `decomposer-gates.json`).

---

### Findings

#### F1 — seed-step.cmd defaults EVERY source to the ON-CONFLICT bulk-fresh lane (invention-violation)
`scripts/win/seed-step.cmd:30`
```
if not defined LAPLACE_BULK_FRESH set "LAPLACE_BULK_FRESH=1"
```
SEVERITY: HIGH — CATEGORY: invention-violation / fork
CLAIM: The canonical seed path (`seed-step` → `seed-stage` → `seed-ladder` → `seed-full`/
`seed-substrate`/`e2e-master`) turns on `LAPLACE_BULK_FRESH=1` by default for every source. The
script's own comment (lines 26-30) states this makes "the DB get a plain COPY stream + ON CONFLICT
DO NOTHING for entities as the only dedup mechanism." Invariant 7 forbids exactly this: "NO
`ON CONFLICT`, NO per-row anti-join — the descent already proved the set novel; re-checking is the
mistake."
VERIFIED: traced the env var into code. `IngestCommands.cs:498` `bool bulkFresh = force ||
IsEnvEnabled("LAPLACE_BULK_FRESH")` → `new NpgsqlSubstrateWriter(ds, bulkFreshSource: bulkFresh)`
(:499) and `IngestRunner.cs:83` `IntentStage.SetBulkFreshBypass(options.BulkFresh)`.
`IntentStage.cs:196-198` confirms: in bulk-fresh the proven-set bank is skipped and "DB uniqueness
is guaranteed by `ON CONFLICT DO NOTHING` / `NOT EXISTS`, not the proven-set." So the default seed
path is the anti-join/ON-CONFLICT lane, not the top-down present-trunk descent the architecture
mandates. (Also note `--force` silently forces the same lane: `force || ...`.) This is the single
most architecturally significant issue in the bucket: the scripts wire the forbidden lane on as the
default. CONFIDENCE: high (env→C# path fully traced).

#### F2 — decomposer-gates.json sets vacuous min:1 floors; ConceptNet & Atomic2020 gates pass on a single row
`scripts/decomposer-gates.json:112-129` (and :17-25, :58, :135-146)
SEVERITY: HIGH — CATEGORY: fake-test / weak-invariant
CLAIM: Many consensus gates use `"min": 1`, which the evaluator treats as "≥1 consensus row of this
relation exists anywhere." For **conceptnet** (`CAPABLE_OF` min 1, `RELATED_TO` min 1) and
**atomic2020** (`X_INTENT` min 1, `OBSTRUCTED_BY` min 1) *every* gate is min 1 — so a source with
tens of millions of assertions passes its full gate set on a single attestation. Same vacuity for
unicode (all three gates min 1), omw `HAS_LANGUAGE` min 1, ud `ENHANCED_DEPENDS_ON` min 1,
wiktionary `IS_COORDINATE_TERM_WITH`/`HAS_USAGE_REGISTER` min 1. These floors are "so low they pass
vacuously," exactly the charter concern.
VERIFIED: `decomposer-gate-check.py:125-167` — `n = consensus_count(relation_type_id(rel)); passed =
n >= minimum`. min 1 ⇒ any single row passes.
CONFIDENCE: high.

#### F3 — consensus gates are GLOBAL per relation type, not scoped to the source under test (wrong invariant)
`scripts/decomposer-gate-check.py:129-137` (consumes `decomposer-gates.json`)
SEVERITY: MEDIUM — CATEGORY: fake-test / weak-invariant
CLAIM: The primary consensus check calls `laplace.consensus_count(relation_type_id(rel))` with **no
source filter**. Only the optional `source_evidence` fallback (:139-159) is source-scoped. So a
source can "pass" its own gate on rows contributed by a *different* source that emits the same
relation. `CORRESPONDS_TO` (min 1000) is emitted by **mapnet, wordframenet, AND semlink** — so
mapnet's gate can be satisfied entirely by semlink's rows (and vice-versa); the gate does not prove
the source-under-test produced anything. The gate proves "this relation exists in the DB," not "this
decomposer worked."
VERIFIED: traced gate JSON relations vs gate-check SQL; the three `CORRESPONDS_TO` sources are in
`decomposer-gates.json:88-110`.
CONFIDENCE: high (the SQL has no source predicate on the main path).

#### F4 — test-all.cmd swallows verify-fk failure then prints "ALL TEST LAYERS PASSED"
`scripts/win/test-all.cmd:16-21`
```
"%PGBIN%\psql.exe" ... -f scripts\verify-fk.sql || (
  echo verify-fk skipped or failed — laplace DB may not exist yet
)
...
echo ALL TEST LAYERS PASSED
exit /b 0
```
SEVERITY: MEDIUM — CATEGORY: correctness / masked-failure
CLAIM: A genuine FK-integrity failure in `verify-fk.sql` is caught by `||` and downgraded to an echo;
the script still falls through to "ALL TEST LAYERS PASSED" and `exit /b 0`. The final aggregate
"all passed" banner is therefore not conditional on the FK check. (The earlier gtest/regress/dotnet
steps are correctly gated with `|| exit /b 1`; only this last step is swallowed.)
VERIFIED: read in full; no exit-code propagation in the `||` block.
CONFIDENCE: high.

#### F5 — omw-bench.ps1 exercises a forked "legacy" ingest lane + commit-parallelism modes
`scripts/win/omw-bench.ps1:19-24,38-39`
```
@{ Name = 'legacy+unordered'; Legacy = '1'; Commit = 'unordered'; ... }
@{ Name = 'legacy+serial';    Legacy = '1'; Commit = 'serial'; ... }
...
$env:LAPLACE_OMW_LEGACY = $cfg.Legacy
if ($cfg.Commit) { $env:LAPLACE_INGEST_COMMIT_PARALLELISM = $cfg.Commit }
```
SEVERITY: MEDIUM — CATEGORY: fork
CLAIM: The bench drives a `LAPLACE_OMW_LEGACY` lane and `LAPLACE_INGEST_COMMIT_PARALLELISM`
serial/unordered modes — the multiple-commit-lane / legacy-fold-lane "disease" CLAUDE.md §2 names
("multiple record writers, commit lanes, fold lanes — each new lane is the disease"). The bench
existing implies these forked lanes still live in the code.
VERIFIED: env knobs read directly; the forks themselves live in C# (not in this bucket — flagged for
the C# auditor). CONFIDENCE: med (script confirms the knobs; lane code not traced here).

#### F6 — omw-bench.ps1 never fails on a nonzero/crash ingest exit (masked failure)
`scripts/win/omw-bench.ps1:48-53`
SEVERITY: MEDIUM — CATEGORY: masked-failure
CLAIM: It records `$proc.ExitCode` into the CSV but never checks it — no `exit`, no `Write-Error`. An
AccessViolation (0xC0000005) or any crash is silently logged and the loop continues to "OMW BENCH
DONE." Contrast `conceptnet-bench.ps1:47-112`, which has `Test-FatalExit` and stops on crash/negative
codes. The two sibling bench scripts disagree on failure handling (drift).
VERIFIED: read both in full. CONFIDENCE: high.

#### F7 — decomposer-promote.cmd promotes into the CANONICAL db even with no passing gate report
`scripts/win/decomposer-promote.cmd:17-26`
```
if exist "!REPORT!" ( ...sys.exit(0 if r.get('passed') else 1)... ) else (
  echo WARN: no gate report at !REPORT! — proceeding anyway
)
```
SEVERITY: MEDIUM — CATEGORY: weak-invariant
CLAIM: If the per-source gate report is missing, promote into the cumulative canonical `laplace` DB
proceeds with only a WARN. A direct `decomposer-promote <src>` (not routed through
`decomposer-matrix`, which does test-first) can write unproven data into the canonical DB.
VERIFIED: read in full. CONFIDENCE: high.

#### F8 — gen-sql.ps1 module-refresh silently rewrites CREATE TABLE/INDEX → IF NOT EXISTS (schema drift)
`scripts/win/gen-sql.ps1:99-101`
```
$sql = $sql -creplace 'CREATE (UNLOGGED )?TABLE (?!IF NOT EXISTS)', 'CREATE $1TABLE IF NOT EXISTS '
$sql = $sql -creplace 'CREATE (UNIQUE )?INDEX (?!IF NOT EXISTS)', 'CREATE $1INDEX IF NOT EXISTS '
$sql = $sql -creplace '(?m)^\s*SELECT pg_extension_config_dump.*$', ''
```
SEVERITY: LOW-MEDIUM — CATEGORY: correctness
CLAIM: The hot `refresh-substrate-module.cmd` path (live SQL refresh against a running DB) rewrites
table/index DDL to `IF NOT EXISTS`. If a table/index *definition changed*, the refresh is a silent
no-op for that object — the operator believes the module refreshed but the altered shape never lands.
A convenience that can mask schema drift during iterative dev.
VERIFIED: read in full. CONFIDENCE: high (behavior is exact-string).

#### F9 — Hardcoded `postgres`/`postgres` credentials across many scripts
`scripts/win/env.cmd:19,24`; `decomposer-test.cmd:53,95`; `decomposer-promote.cmd:31,49`;
`ensure-floor.cmd:20,26`; `gen-sql.ps1:107`; `seed-layer-check.ps1:19`; `status.ps1:8`;
`wait-for-pg.ps1:12`; plus every `psql -U postgres` invocation.
SEVERITY: LOW — CATEGORY: other (secret) — dev-sandbox, low priority per CLAUDE.md §audit-priority
CLAIM: `Password=postgres` / `PGPASSWORD=postgres` are baked in as defaults. `env.cmd:20` does carry
a comment that production must override `PGPASSWORD`/`LAPLACE_DB`, and the defaults are guarded with
`if not defined`, so they are overridable. Noted for completeness; auth is explicitly low-priority.
VERIFIED: read in full. CONFIDENCE: high.

#### F10 — build-engine-asan.cmd hardcodes UCD data paths instead of %LAPLACE_DATA_ROOT% (drift)
`scripts/win/build-engine-asan.cmd:48-51` vs `scripts/win/build-engine.cmd:30,39-42`
SEVERITY: LOW — CATEGORY: fork / drift
CLAIM: `build-engine.cmd` derives UCD paths from `%LAPLACE_DATA_ROOT%`; the ASAN sibling hardcodes
`D:/Data/Ingest/UCD/...` literally. The two near-identical build scripts have diverged — a vault
moved via `LAPLACE_DATA_ROOT` breaks the ASAN build only. Also both hardcode the VS-2026 ninja path
and PG18 libxml2 paths (machine-specific, not env-driven). CONFIDENCE: high.

#### F11 — e2e-master.cmd dead goto
`scripts/win/e2e-master.cmd:37-39`
```
if "%DB_ONLY%"=="1" goto phase_db_bootstrap
:phase_db_bootstrap
```
SEVERITY: INFO — CATEGORY: dead-code
CLAIM: The conditional `goto` targets the label on the very next line — a no-op jump regardless of
`DB_ONLY`. (The meaningful `DB_ONLY` early-exit is the later check at :44-48.) Harmless but dead.
CONFIDENCE: high.

#### F12 — Diagnostic scripts use blanket `$ErrorActionPreference = 'SilentlyContinue'`
`scripts/win/status.ps1:4`, `scripts/win/locks.ps1:7`
SEVERITY: INFO — CATEGORY: other
CLAIM: Both read-only diagnostic scripts suppress all errors globally. Acceptable for status/lock
inspection (they probe processes/PG that may be absent), but it means a genuine failure inside them
surfaces as blank output rather than an error. CONFIDENCE: high.

#### F13 — db-clone.cmd is a deprecation tombstone (intentional dead file)
`scripts/win/db-clone.cmd` — whole file just echoes "deprecated" and `exit /b 2`.
SEVERITY: INFO — CATEGORY: dead-code
CLAIM: Intentional guard redirecting users to `decomposer-test`/`decomposer-promote`. Not a defect;
noted as an orphaned-by-design script. CONFIDENCE: high.

---

### Clean / no findings
`cli.cmd`, `status.cmd`, `converse.cmd`, `ingest-repo.cmd`, `ingest-text.cmd`, `index-content.cmd`,
`publish.cmd`, `serve.cmd`, `verify-deploy.cmd`, `regress.cmd`, `test-engine.cmd`, `test-app.cmd`,
`tree-lock.ps1`, `wait-for-pg.ps1`, `seed-layer-check.ps1`, `codegen-attestation-law.ps1`,
`prompts_smoke.txt`, `witness-manifest.json` (doc-manifest; prose only, no executable invariant),
`build-engine-libs.cmd`, `build-extensions.cmd`, `build-web.cmd` (robocopy `errorlevel 8` check is
correct), `db-isolate.cmd`, `db-reset.cmd`, `install-extensions.cmd` (hot-swap rename-on-lock is
sound), `tune-pg.cmd`, `walk-verdict.cmd`, `seed-*` orchestrators (correct `|| exit /b 1`
propagation throughout), `decomposer-matrix.cmd`, `decomposer-test.cmd`, `run-decomposer-matrix-logged.ps1`.
These propagate exit codes correctly and contain no fake-success / swallow patterns.

---

### Bucket summary
- HIGH: 2 (F1, F2)
- MEDIUM: 5 (F3, F4, F5, F6, F7)
- LOW-MEDIUM: 1 (F8)
- LOW: 2 (F9, F10)
- INFO: 3 (F11, F12, F13)

WORST ISSUE: **F1** — `seed-step.cmd:30` defaults `LAPLACE_BULK_FRESH=1` for every source, which
(traced into `IngestCommands.cs`/`IngestRunner.cs`/`IntentStage.cs`) forces the ingest down the
`ON CONFLICT DO NOTHING` / per-row anti-join lane that invariant 7 explicitly forbids. The canonical
seed path is wired to the forbidden lane by default. F2 (vacuous min:1 gates for whole sources like
ConceptNet/Atomic2020) is the second-worst: the proof gates can pass on a single row, so a broken
decomposer can clear them.
