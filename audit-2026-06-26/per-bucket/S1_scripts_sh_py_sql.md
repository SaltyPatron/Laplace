# Bucket: S1 — scripts/*.sh, *.py, *.sql, scripts/laplace, *.example

### Files read (57 — all read IN FULL)
- [x] scripts/agent-anchor.sh
- [x] scripts/audit-decomposers.sh
- [x] scripts/book-receipts.sql
- [x] scripts/bootstrap-attestation-manifest.py
- [x] scripts/bootstrap-laplace-runner.sh
- [x] scripts/bootstrap-stripe-dev.sh
- [x] scripts/check-prereqs.sh
- [x] scripts/codegen-attestation-law.py
- [x] scripts/configure-github-repo.sh
- [x] scripts/converse.sh
- [x] scripts/decode-probe.py
- [x] scripts/decomposer-clone.sh (deprecated stub — exits 2)
- [x] scripts/decomposer-ensure-floor.sh
- [x] scripts/decomposer-gate-check.py
- [x] scripts/decomposer-isolate-plan.py
- [x] scripts/decomposer-isolate.sh
- [x] scripts/decomposer-ladder-ci.sh
- [x] scripts/decomposer-matrix.sh
- [x] scripts/decomposer-promote.sh
- [x] scripts/decomposer-test.sh
- [x] scripts/e2e-substrate.sh
- [x] scripts/forward-pass.sql
- [x] scripts/foundry-probe.py
- [x] scripts/import-tree-sitter-grammars.sh
- [x] scripts/ingest-source.sh
- [x] scripts/laplace (bash launcher)
- [x] scripts/laplace-bench.sql
- [x] scripts/laplace.env.example
- [x] scripts/llama_behavioral.sh
- [x] scripts/migrate-decomposer-attest.py (one-shot codemod — orphaned)
- [x] scripts/model-forward-oracle.py
- [x] scripts/model-synthesize-ci.sh
- [x] scripts/model-synthesize.sh (exec wrapper)
- [x] scripts/normalize-submodule-attributes.sh
- [x] scripts/probe-ffn-concepts.py
- [x] scripts/python/download_code_data.py
- [x] scripts/queries/factcheck.sql
- [x] scripts/setup-host.sh
- [x] scripts/sql/converse-audit.sql
- [x] scripts/sql/deblob-convergence-audit.sql
- [x] scripts/sql/debug-france-pipeline.sql
- [x] scripts/sql/debug-order-closure.sql
- [x] scripts/sql/model-planes-audit.sql
- [x] scripts/sql/normalization-audit.sql
- [x] scripts/sql/normalize-deprel-names.sql
- [x] scripts/sql/probe-capital-france-paris.sql
- [x] scripts/sql/probe_inference.sql
- [x] scripts/sql/rebuild-consensus-indexes.sql
- [x] scripts/sql/substrate-audit.sql
- [x] scripts/sql/substrate-law-probes.sql
- [x] scripts/substrate-inference-demo.sql
- [x] scripts/sync-external.sh
- [x] scripts/test-integration.sh (exec wrapper → model-synthesize-ci.sh)
- [x] scripts/validate-pipeline.py
- [x] scripts/verify-fk.sql
- [x] scripts/verify-pg-postgis.sh
- [x] scripts/wordnet-receipts.sql

---

## Findings

### 1. scripts/bootstrap-attestation-manifest.py — BROKEN codegen, silently zeroes the manifest
- **FILE:LINE**: scripts/bootstrap-attestation-manifest.py:40-54, 149-156
- **SEVERITY**: HIGH
- **CATEGORY**: correctness / dead-code / silent-data-loss
- **CLAIM**: This generator parses `app/Laplace.Decomposers.Abstractions/RelationTypeRegistry.cs` for a `Dictionary<string, RelationTypeDef> Canon` block and a `Dictionary<string, (string Canon, bool Flip)> Alias` block, then OVERWRITES the checked-in `engine/manifest/relation_types.toml` and `pos_tags.toml`. The C# registry has been refactored and no longer contains those structures (it now resolves relations from the native engine via `NativeInterop.RelationCanonicalForTypeId` / `RelationManifestCanonical`). So `parse_canon`/`parse_aliases` match **nothing**, and the script writes an essentially empty manifest.
- **VERIFIED**: I RAN it. Output: `Wrote 0 relations, 0 aliases`. `git diff --stat` showed it rewrote `relation_types.toml` from 1242 lines → ~8 (`8 insertions, 1293 deletions`) and stripped `pos_tags.toml`. I `git checkout`-restored both (tree is clean again). Then grepped the registry: `Dictionary<string, RelationTypeDef> Canon` and `new(RelationTypeRank...` — **No matches found**; the registry's records are now `(Hash128 Id, double Rank, Symmetry, bool Flip, Hash128? ParentId, string Canonical)` resolved from native (`RelationTypeRegistry.cs:13,37,162`). The data-flow has inverted (engine TOML → C# now), so this script's input contract is dead, yet it exits 0 with a success-looking message. Anyone running it (the codegen header comments instruct "run codegen-attestation-law.ps1 after edits"; this is the upstream half) destroys the manifest.
- **CONFIDENCE**: high

### 2. engine generated identity uses the forbidden `substrate/type/X/v1` namespace
- **FILE:LINE**: scripts/codegen-attestation-law.py:400 (`"substrate/type/%s/v1"`), :677 (`"substrate/pos/%s/v1"`), :560/:565-573 (seed SQL emits `substrate/type/<name>/v1`); committed output confirmed at engine/core/src/generated/relation_law.c:200 and pos_law.c:147
- **SEVERITY**: MEDIUM
- **CATEGORY**: invention-violation
- **CLAIM**: Charter item 6 / CLAUDE.md: concepts and relation types must be `blake3` of the **real inventory name** (GWN/ConceptNet name, UPOS), "never an invented `substrate/type/X/v1` namespace." The codegen hashes `snprintf("substrate/type/%s/v1", canonical_name)` rather than the bare canonical name, i.e. it manufactures exactly the prohibited namespace, then seeds those strings into `canonical_names`. POS does the same with `substrate/pos/%s/v1`.
- **VERIFIED**: Read codegen-attestation-law.py `type_id_from_canonical` (:397-404) and `emit_pos_law` (:677); grepped the committed generated C — both literals present. This is the relation/POS **identity** path (not synset/ILI), and it's a closed registry rather than a convergence anchor, so the practical harm is narrower than for ILI; but it is the literal pattern the charter names as a violation, and several SQL audit scripts (deblob-convergence-audit.sql:16-24, normalize-deprel-names.sql:22-45) depend on it, propagating the namespace into IS_A category anchors (`substrate/type/WordNet_Synset/v1`, etc.).
- **CONFIDENCE**: med (high that the namespace is used; med that it's "wrong" vs. an accepted convention for closed registries — report both)
- Note: `codegen-attestation-law.py` itself is NOT drifted — I ran it and `git status` showed no change vs. committed `relation_law.c`/`pos_law.c`/headers/seed-SQL. Only the *upstream* generator (finding #1) is broken.

### 3. scripts/bootstrap-laplace-runner.sh — stray top-level call runs systemd mutations on EVERY mode
- **FILE:LINE**: scripts/bootstrap-laplace-runner.sh:294 (`bootstrap_runner_oom_guard` invoked at file scope, between two function definitions, before the `case "$MODE"` dispatch and before `require_root`)
- **SEVERITY**: MEDIUM
- **CATEGORY**: correctness
- **CLAIM**: `bootstrap_runner_oom_guard` (mkdir `/etc/systemd/system/$RUNNER_SERVICE.d`, write drop-in, `systemctl daemon-reload`) is called unconditionally at line 294 as a side-effect of sourcing the script body. So `status` (documented "no changes"), `--help`/`usage`, `stripe`, and `reset` all execute this mutation. Under `set -eo pipefail`, running `status`/`--help` as a non-root user fails at the `mkdir`/`systemctl` and aborts before printing anything; run as root it silently performs a write + daemon-reload on a pure-query invocation.
- **VERIFIED**: Read the file. Line 294 is a bare function call outside any function and outside the `case` at :1135. `do_status`/`usage` paths never re-invoke it intentionally; the function is otherwise never called from `do_bootstrap` (:1049-1075 does not list it), so line 294 is the only caller — clearly a misplaced/accidental top-level call.
- **CONFIDENCE**: high

### 4. scripts/decomposer-gate-check.py — gates can pass vacuously via env/fallback
- **FILE:LINE**: scripts/decomposer-gate-check.py:76-79 (`allow_health_tier` forces `health_ok=True`), :194-197 (`LAPLACE_GATE_ALLOW_HEALTH_TIER=1`); :139-159 (`consensus` gate falls back to `source_evidence`)
- **SEVERITY**: MEDIUM
- **CATEGORY**: fake-test / invention-violation
- **CLAIM**: `substrate_health()` includes the tier-law check (charter item 3: tier≠kind). When `LAPLACE_GATE_ALLOW_HEALTH_TIER=1`, a failing health result is overwritten to pass ("tier violations allowed"). That converts the gate guarding the tier invariant into a no-op via an env var. Separately, a `consensus`-count gate that fails can be rescued by summing `source_evidence` across `fallback_relations` with a possibly-lower `fallback_min` — softening the consensus assertion. A green gate report therefore does not guarantee the invariant held.
- **VERIFIED**: Read the gate logic; `record("substrate_health", health_ok=True, ...)` after the override (:76-79); fallback branch widens `passed` (:154-159). Gate thresholds/relations come from `scripts/decomposer-gates.json` (not in this bucket) interpolated as f-strings into SQL — trusted source, but see #8.
- **CONFIDENCE**: high (mechanism); med (severity — depends on whether the env flag is set in CI)

### 5. scripts/forward-pass.sql — graph traversal as recursive SQL (logic in SQL, not engine)
- **FILE:LINE**: scripts/forward-pass.sql:20-47 (`WITH RECURSIVE walk ...` doing the generation/inference walk)
- **SEVERITY**: MEDIUM
- **CATEGORY**: altitude / invention-violation
- **CLAIM**: Charter item 5 (engine holds all logic; SQL is a thin orchestrator) and the repo's own hard-stop list (agent-anchor.sh:61 "Compiled cascade SRF only — no recursive SQL / cursor / app-loop graph traversal"). `forward_pass` implements the inference/generation walk as a `WITH RECURSIVE` CTE over `consensus`, with per-step `ORDER BY ... LIMIT 1` and plane/band weighting in SQL. This is exactly the recursive-SQL graph traversal the hard-stop forbids, at the heart of the "transformer operation" the substrate is meant to make an indexed engine read. Same pattern recurs in demo/probe SQL (wordnet-receipts.sql:38-39 `generate` recursive-CTE walk; substrate-inference-demo.sql query-time bilinear read).
- **VERIFIED**: Read the function body; it is a defined `laplace.forward_pass(...)` (a live function, not just a probe). Cross-checked the hard-stop text in agent-anchor.sh. The file's own header admits the WS3 gap (synsets string-addressed) so it is known-provisional.
- **CONFIDENCE**: med (the hard-stop is a .cursorrules/Claude-authored rule, but it aligns with charter item 5)

### 6. scripts/model-synthesize-ci.sh — only proxy for synthesis quality is file size
- **FILE:LINE**: scripts/model-synthesize-ci.sh:80-85 (`size > 50000000` ⇒ PASS)
- **SEVERITY**: LOW
- **CATEGORY**: fake-test
- **CLAIM**: This is otherwise a GOOD real test (idempotency: pass-2 must short-circuit :49-53; evidence ≥1000 per relation :64-67; consensus>0 :69-70). But the synthesis "quality" assertion is purely `GGUF size > 50 MB`. A large-but-incoherent GGUF passes. The actual coherence probe (`foundry-probe.py`, which measures testimony transfer / separation-sigma) is NOT invoked in CI. So CI proves the pipeline runs and the file is big, not that the cast carries the substrate's testimony.
- **VERIFIED**: Read both scripts. foundry-probe.py exists and is the real fidelity test but is not called from any CI script in this bucket.
- **CONFIDENCE**: high

### 7. scripts/laplace-bench.sql — latency-only bench; proves nothing about correctness
- **FILE:LINE**: scripts/laplace-bench.sql (whole file); disparagement at :82-88 ("KNOWN-DEGRADED", "DEGRADED")
- **SEVERITY**: LOW
- **CATEGORY**: fake-test / disparagement
- **CLAIM**: The bench records `latency_ms` and `rowcount` per "transformer-class operation" but asserts nothing — a query returning 0 rows still "passes" (the row is recorded; on timeout/error the row is silently skipped via `ON_ERROR_STOP off`). It is a perf demo on a pre-populated DB, not a correctness gate; reading it as "substrate works" would be wrong. Also carries static "KNOWN-DEGRADED / DEGRADED" comment tags (Claude-authored editorializing per charter §0).
- **VERIFIED**: Read the file; the macro inserts unconditionally, `\set ON_ERROR_STOP off` (:14) drops failed tests silently.
- **CONFIDENCE**: high

### 8. SQL f-string interpolation of identifiers across the Python gate/orchestration
- **FILE:LINE**: decomposer-gate-check.py:88, 102-106, 113-118, 132-152 (decomposer/relation names f-string'd into SQL); substrate via `laplace.source_id('{decomposer}')` etc.
- **SEVERITY**: LOW
- **CATEGORY**: correctness (injection surface)
- **CLAIM**: Source/relation names are interpolated directly into SQL strings rather than parameterized. Values originate from checked-in `decomposer-gates.json` and `--source` CLI arg, so this is not a live exploit, but it is an unsafe pattern: any name containing a quote breaks/injects. Same shape in audit-decomposers.sh / decomposer-ensure-floor.sh (`canonical_id('substrate/type/.../${n}/v1')`).
- **VERIFIED**: Read the call sites; no quoting/escaping, no bind params.
- **CONFIDENCE**: high (pattern); low (exploitability — trusted inputs)

### 9. Security posture: pg_hba `trust` to the whole LAN + cleartext Stripe key handling
- **FILE:LINE**: bootstrap-laplace-runner.sh:533-543 (`host all all <LAN_CIDR> trust`, default `192.168.1.0/24`, `listen_addresses='*'`), :376-378 (Stripe key written to runner `.env`)
- **SEVERITY**: LOW
- **CATEGORY**: other (security; dev-sandbox per charter audit-priority)
- **CLAIM**: When `LAPLACE_PG_LAN_CIDR` is set (default 192.168.1.0/24), the cluster listens on `*` and grants password-less `trust` to every host on the LAN as any role including `laplace_admin` (SUPERUSER). Stripe test key is written into the runner `.env` in cleartext (mode is left to the runner). Charter marks auth/billing as dev-sandbox / low priority, so noting, not escalating.
- **VERIFIED**: Read the pg_hba heredoc and Stripe env block.
- **CONFIDENCE**: high

### 10. Hardcoded host-specific paths
- **FILE:LINE**: book-receipts.sql:12 (`SET ...perfcache_path = 'D:/Data/Postgres/laplace/share/laplace_t0_perfcache.bin'`); download_code_data.py:21-22 (`D:/Data/Ingest/...` defaults); llama_behavioral.sh:16 (`/data/archive/llama-workspace/...` default bin)
- **SEVERITY**: LOW
- **CATEGORY**: other / portability
- **CLAIM**: Windows/absolute machine paths baked as defaults. Overridable in most cases (env/args) but book-receipts.sql hardcodes a Windows perfcache path in a `SET` with no override.
- **VERIFIED**: Read each line.
- **CONFIDENCE**: high

### 11. Dead / orphaned / superseded scripts
- **FILE:LINE**: decomposer-clone.sh (whole — prints "deprecated", exit 2); migrate-decomposer-attest.py (one-shot codemod, no guard, edits *.cs in place); normalize-deprel-names.sql + debug-france-pipeline.sql + debug-order-closure.sql + probe-capital-france-paris.sql (scratch debug over PRECEDES bigrams / `trajectory_pairs`, which CLAUDE.md says is being retired); book-receipts.sql (Moby-Dick PRECEDES-bigram demo); bootstrap-attestation-manifest.py (broken, finding #1)
- **SEVERITY**: LOW
- **CATEGORY**: dead-code
- **CLAIM**: These are non-load-bearing scratch/one-shot/deprecated. The bigram/PRECEDES debug SQL contradicts the stated direction (retire the bigram generator); they are dev artifacts, harmless but stale. `migrate-decomposer-attest.py` does an unguarded text-replace across `app/**/*.cs` — re-running after the refactor is a no-op but it is a sharp edge with no dry-run.
- **VERIFIED**: Read each.
- **CONFIDENCE**: high

### 12. CILI floor declared in ingest but not enforced in test/audit floors (observation)
- **FILE:LINE**: ingest-source.sh:14 (`FLOOR=(unicode iso639 cili)`, comment: "CILI must precede wordnet/omw") vs decomposer-ensure-floor.sh:21 (ensures only unicode+iso639) and audit-decomposers.sh:103-134 / decomposer-ladder-ci.sh:16 (LADDER floor = unicode iso639 only)
- **SEVERITY**: INFO
- **CATEGORY**: invention-violation (convergence-index backbone) / consistency
- **CLAIM**: CLAUDE.md calls CILI/ILI "the canonical sin" and the convergence backbone that must precede WordNet/OMW for maximal convergence. `ingest-source.sh all` puts `cili` in the floor, but the per-decomposer test floor and the CI ladders do not ensure CILI before running wordnet/omw (prerequisites actually come from `decomposer-gates.json`, not in this bucket — could not confirm whether cili is wired there).
- **VERIFIED**: Read the three floor definitions; they disagree. Could not check decomposer-gates.json (out of bucket).
- **CONFIDENCE**: med (the inconsistency is real; the impact depends on gates.json)

---

### Bucket summary
- CRITICAL: 0
- HIGH: 1 (#1 broken upstream codegen silently wipes the relation/POS manifest)
- MEDIUM: 4 (#2 forbidden namespace, #3 stray systemd mutation on every mode, #4 gates pass vacuously via env/fallback, #5 recursive-SQL traversal)
- LOW: 6 (#6 size-only synthesis check, #7 latency-only bench, #8 SQL f-string interpolation, #9 LAN trust auth, #10 hardcoded paths, #11 dead scripts)
- INFO: 1 (#12 CILI floor inconsistency)

**Worst issue**: #1 — `bootstrap-attestation-manifest.py` is a checked-in codegen script whose input contract (the old C# `Canon`/`Alias` dictionaries) no longer exists after the registry was refactored to read from the native engine. Running it (which the codegen flow nominally chains) silently parses 0 relations, prints a success-shaped message, exits 0, and overwrites the 1242-line source-of-truth `relation_types.toml` with a near-empty file (verified by running it: "Wrote 0 relations"; 1293 deletions; restored via git checkout). `codegen-attestation-law.py` (TOML→C) was verified NOT drifted.
