# Laplace — full-repo audit (2026-06-26)

Branch `chess-modality` @ `6ac9952`. Auditor: Claude (Opus 4.8), 26 parallel readers + direct verification.

This document is itself a *claim* and must be held to the same bar as everything else in the repo:
every load-bearing finding below was traced to the **code that compiles/runs**, not to comments,
plans, or prior audits. Per the repo's own §0 epistemic contract, the existing `AUDIT-*.md` and
`AGENT_*_REPORT.md` files were treated as artifacts to be audited, not as sources — and several are
found wrong/harmful (see §6). The granular per-file detail lives in `per-bucket/*.md` (4,656 lines).

---

## 0. Method & coverage proof

- **1026 first-party files** (every tracked file except the 310 vendored `external/` submodule files —
  tree-sitter grammars + 10 vendored libs, legitimately out of scope; see I1 detail). Manifest:
  `coverage-manifest.txt`.
- Partitioned into **26 non-overlapping buckets** (verified: 1026 covered, 0 uncovered, 0 duplicated).
- **Every file in every bucket was opened and read in full** by its reader (large generated parser
  tables and >256 KB rotating logs were paged/grepped end-to-end and disclosed as such).
- Each reader bound to a shared charter (`CHARTER.md`): the 8 invention invariants + ordinary-defect
  classes + the rule that comments/docs/tags are claims to verify, never truth.
- The highest-severity findings were then **re-verified by me directly** (§1 "verification log").

Severity tally across all buckets (deduplicated to systemic themes in §3):
**CRITICAL 3 · HIGH ~22 · MEDIUM ~45 · LOW ~40 · INFO/positive ~30.**

---

## 1. Verification log (claims I re-checked against code myself, not via the agents)

| Claim | Verified at | Result |
|---|---|---|
| `EntityTier.Vocabulary = 5` (tier-as-kind) | `app/Laplace.SubstrateCRUD/EntityTier.cs:20` | **Confirmed.** Comment rationalizes jamming KIND into the depth axis. |
| `apply_batch` uses ON CONFLICT despite swearing it doesn't | `extension/laplace_substrate/src/apply_batch.c:14-35` (header: "NO ON CONFLICT" ×3) vs `:137,:170` (`ON CONFLICT (id) DO NOTHING`) | **Confirmed self-contradiction + invariant-7 violation.** |
| Audio/Image decomposers are empty stubs registered as live | `app/Laplace.Decomposers.Audio/AudioDecomposer.cs:34-42` (`#pragma warning disable CS1998` … `yield break`) | **Confirmed.** |
| Invented namespace baked into generated C | `engine/core/src/generated/relation_law.c:200` (`"substrate/type/%s/v1"`), `pos_law.c:147` | **Confirmed.** |
| Ingest "proofs" document failure as success | `.ingest-proof/phase8-ladder.out` (`ud exit=-1073741819 … 11.51 GB … STOP: ud failed`) vs `.ingest-proof/PHASE8-SUMMARY.tsv` (4 green rows, UD + 5 sources omitted) | **Confirmed.** Two inconsistent summaries; the .tsv manufactures green by omission; 11.5 GB refutes the Pi-RAM mandate; the same log shows the period-fold **drain** is the live path. |

---

## 2. Holistic verdict — does the build deliver the invention?

**The invention is real, and its hardest core is correctly built.** This is the single most important
finding and it is not hedged: the content-addressed Merkle-DAG and the Glicko-2 denoiser are
*genuinely implemented*, not faked.

- Identity = `blake3(content)` with tier explicitly excluded from the id (`hash128_merkle`, `(void)tier`);
  provenance lives in attestations; codepoint/physicality ids are source-free. (E1, verified.)
- Tier = `max(child)+1`, emergent in compose; trajectory packing is bit-exact lossless; Hilbert
  round-trips; the Merkle trunk short-circuit prunes whole subtrees top-down. (E1/E2.)
- Glicko-2 math is correct, fixed-point ×1e9, `eff_mu = rating − 2·rd`, pinned to the Glickman paper
  intermediates; the closed-form fold == the per-observation loop. (E1/E2 real tests.)
- The "generalized transformer weights" dynamics (QK/OV/FFN GEMMs, Laplacian eigenmaps, gram-schmidt,
  procrustes) are real native math (MKL/Eigen/Spectra), with real recovery tests. (E3.)
- The geometry extension is a correct thin marshaller; geometry is used as **form**, all reads route
  **meaning** through `eff_mu`/consensus, never coordinates. (X1/X3.)
- The API is honestly GPU-free: **no hidden call to any external LLM exists anywhere** in the app or
  web — every inference/embedding path is parameterized SQL against `laplace.*`. The two-level
  embedding (form = S³, meaning = consensus-NN) is correctly kept separate. (A2/I2, verified by grep.)

**Where the build does *not yet* deliver the whole claim** — every item is *drift from the design*,
not a flaw in the design, and every item is enumerable and fixable:

1. **The convergence index — the backbone — is keyed on an invented `substrate/type/X/v1` namespace,
   not on the bare external ids (ILI/UPOS/ISO-639/GWN) the design mandates.** It is baked into
   generated C, the SQL seed/bootstrap, C# registries, *and pinned by tests as "Law."* Consequence:
   cross-source convergence holds *within* this system (one resolver), but the "linguistic highways"
   do **not** connect to anything anchored on the real external id. The instance anchors that matter
   most (synsets→real ILI string, sense keys) **are** correct (A6 verified against real
   `ili-map-pwn30.tab`), so the backbone is *partially paved, partially corrupt* — exactly the WS3
   debt the repo names, now confirmed live. **Worse:** ConceptNet opts out of the ILI index entirely
   (word↔word surface; the synset bridge is dead on the live path), and WordNet's CILI gate is
   *warn-only* — a missing map silently ingests orphan words with zero synsets while reporting success
   (A6). The one source that *must* converge (the backbone) has the weakest gate.

2. **Tier-as-kind pollutes the geometry axis for exactly the backbone nodes.** `Vocabulary=5` is
   stamped on every meta/vocabulary/convergence-index node, repo-wide and structurally locked
   (SQL `VOCAB_TIER=5`, `24_identity_health` *blesses* it, tests pin it). The *same* content id gets
   an emergent word-tier from its owning decomposer but tier 5 from a bridge → its stored tier and
   geometric radius is **ingest-order-dependent** (A7). Radius is supposed to *be* compositional depth.

3. **The fold is forked, and the live bulk path is the drain — not the online-inline ideal.** An
   inline path exists (`apply_batch` folds `observation_count`; `FoldIncrementalAsync`, used by chess),
   but it coexists with a catch-up **drain** (`14_period_fold`, `ConsensusAccumulatingWriter`,
   `MaterializeConsensusAsync`, `finish_consensus_fold` full-table rebuild/swap). The proof logs show
   the **period-fold drain is what actually ran** on bulk ingest. So "each attestation improves the
   next query" is not the live behavior. 4–5 mutually-gated fold lanes exist (X1/X2/A11) — the exact
   "fold lanes = the disease" the rules name.

4. **The write path violates its own "no ON CONFLICT / no anti-join" law.** `apply_batch.c` ships
   `ON CONFLICT (id) DO NOTHING`, and a **default-on** bulk-fresh bypass lane (`LAPLACE_BULK_FRESH=1`
   in `seed-step.cmd`, → `IntentStage._bulkFreshBypass`) emits *all* nodes with an empty dedup bitmap
   and leans on ON CONFLICT/NOT EXISTS as the dedup. `23505 unique_violation` is institutionalized as
   an expected, whole-batch-retried conflict — directly against the `conflicts ≈ 0` invariant. (A1,
   A10, S2, X1, I3 — multi-source.) The clean C# writer (`ApplyManyAsync`) is *correct*; the violation
   is inside the SPI function it calls and in the bypass lane wired on by default.

5. **The `<30 min` / Pi `O(batch)` RAM mandate is unmet and the artifacts misreport it.** The full
   multilingual pipeline never completed in any captured run: UD crashes at **11.51 GB** with a native
   **access violation**; the ladder `break`s there; downstream sources never ran; `PHASE8-SUMMARY.tsv`
   shows all-green by omitting the crash. (I4, verified.) This is the most serious *honesty* defect in
   the tree.

6. **Generation/export carries the bigram shape.** The dead degenerate-weight engine code is dead
   (E4), but the *live* lm_head is factored from a single adjacency/PPMI plane (`FoundryExport.cs`
   ~1019/1109) — literal bigram structure — and the SQL bigram generator + `trajectory_pairs` backfill
   still exist (X2). Export is "synthesis, not the product," so this is lower priority, but the claim
   that generation = a traversal of the consensus field is undercut by a bigram lm_head.

7. **The "generic turn-based modality engine" is aspirational.** `laplace_grammar_compose` has exactly
   one non-text modality branch (`is_json_modality`); every other modality grapheme-explodes its
   surface through the UAX-29 floor — the "a board isn't prose" O(rows) category error (E1). Chess
   avoids it only by a **bespoke `ChessCompose` bypass** (correctly, A12) — i.e. genericity was bought
   with a fork. Each new modality needs its own bypass until the generic path folds an arbitrary AST.

8. **A layer of stubs, vacuous gates, false-green tests, and stale-harmful docs hides all of the
   above** (§4, §5, §6). This is what makes the drift hard to see and is why the repo *feels* further
   along than it is.

**Bottom line:** the crystal-ball core exists and works. What stands between it and the full claim is
(a) paving the convergence index onto real external ids and connecting ConceptNet/WordNet to it,
(b) removing tier-as-kind so radius == depth, (c) collapsing the fold/write forks to the single
online-inline trunk, (d) making the generic compose fold any AST, and (e) deleting the stub/gate/
proof/doc layer that currently misrepresents state. None require abandoning the design.

---

## 3. Cross-cutting systemic violations (ranked; each multi-source confirmed)

### CRITICAL

- **C1 — Ingest proofs document failure as success; scale mandate unmet.** `.ingest-proof/` (I4,
  verified §1). UD access-violation @11.5 GB, full pipeline never completed, `PHASE8-SUMMARY.tsv`
  green-by-omission. *Fix:* delete `.ingest-proof/` (git-ignore run artifacts); fix the UD AV and the
  RAM blow-up before any "Phase 8 complete" claim.
- **C2 — Tier-as-kind (`EntityTier.Vocabulary = 5`).** Definition `EntityTier.cs:20`; used by 30+
  files; SQL `10_bootstrap` `VOCAB_TIER=5` + `24_identity_health` blesses it; test-pinned;
  order-dependent tier on shared nodes. (A4 CRITICAL, A8 CRITICAL, A5/A6/A7/A9/A11/X2/I3.) *Fix:* move
  KIND to `type_id`/physicality/trust; let tier be emergent depth only; un-pin the tests.
- **C3 — Convergence index keyed on invented `substrate/type|pos/X/v1`, not real external ids.**
  Generated C (`relation_law.c:200`, `pos_law.c:147`), SQL seed/bootstrap, C# registries
  (`EntityTypeRegistry`, `RelationTypeRegistry`, `BootstrapIntentBuilder`), pinned by `CanonicalPathLawTests`/
  `TypeIdLawTests` as "Law"; string-walked read-side (`content_resolve.c:290`, `LIKE 'substrate/type/%'`
  filters). (E1/E2/A4/A5/A11/X1/X2/S1 — 8 buckets.) *Fix:* WS3 — anchor on bare ILI/UPOS/ISO/GWN ids.

### HIGH

- **H1 — Fold forked + live bulk path is a drain** (not inline). X1/X2/A11/I4. *Fix:* one inline trunk.
- **H2 — Write path: ON CONFLICT + default-on bulk-fresh bypass + 23505-as-expected** (anti-join lane).
  apply_batch.c:137/170; seed-step.cmd:30 `LAPLACE_BULK_FRESH=1`; IntentStage.cs:185-219; A1/A10/S2/X1/I3.
- **H3 — Ingest writer/commit fork lanes.** `IngestRunner` 3+ flag-gated lanes (one a documented
  heap-corruption workaround), `_bulkFreshBypass` second writer. A1/A10.
- **H4 — ConceptNet bypasses the ILI index; WordNet CILI gate is warn-only (silent orphan ingest).** A6.
- **H5 — Generic compose grapheme-explodes every non-JSON modality; chess only fixed via a bespoke
  bypass fork.** E1/A12.
- **H6 — Bigram lm_head + live SQL bigram generator + `trajectory_pairs` backfill** (the foundry
  incoherence shape). E4/X2/A1.
- **H7 — Empty stubs registered as live sources / bound as usable:** Audio + Image decomposers
  (`yield break`), `feature_extractor` (load→null/extract→-1), `arch_template_load` ignores its name
  (every recipe coerced to Llama layout), `TabularDecomposer` hardcoded to one Kaggle dataset
  (`targetColumn="Exited"` → all-negative graph on any other CSV, status=ok). A9/E4.
- **H8 — MKL-optional default → silent no-ops on the live export path.** `compute_substrate_gram`/
  `tensor_svd_truncate` return `-2` without MKL; CMake only warns; synthesis host has no availability
  gate (only Dynamics does). E3/E4/A10/I1.
- **H9 — Fake/false-green tests & gates** (detail in §4). A1/A3/A5/A6/A7/S1/S2/I1.
- **H10 — Broken codegen footgun:** `bootstrap-attestation-manifest.py` parses structures that no
  longer exist, overwrites the 1242-line `relation_types.toml` with near-empty, exits 0 "success"
  (S1 ran it and restored). *Fix:* delete or rewrite against the native source of truth.
- **H11 — Stale/harmful Claude docs that would destroy working code** (detail in §6). X3/I3/I5.
- **H12 — Tatoeba: identity-from-provenance + data race.** Entity id = `tatoeba/sentence/{source id}`;
  shared `HashSet` mutated from parallel compose workers drops translation edges. A8.

### MEDIUM (representative — full list in per-bucket files)

Heavy compute stranded in C#/SQL (altitude): foundry BPE/PPMI/COO/frame-advance in C# (A1),
`forward-pass.sql` recursive-CTE inference walk (S1), SQL-orchestrated fold compute (X2), per-row SPI
in C loops `structural_cluster` ~2000+ round-trips/call (X1), UCDXML parsed twice with divergence risk
(E2), `JsonGrammarHelper.ChildrenOf` O(n²) over AST (bites 9 GB Wiktionary) (A4). — Dual
decomposer/registration lanes (omw/conceptnet/atomic2020/wiktionary bespoke + generic) (A1/A8). —
`/v1/capabilities` golden pins **wrong** backend routing strings; `token_logprobs` populated with
positive n-gram strides (A2/A3). — UD default run unscoped (~4.3 GB every-language) (A8). — PredicateMatrix
hardcodes eng+v, silently drops the rest (A7). — Bridges re-emit foreign-owned anchors → apply-time
conflicts (A7). — `ContentDescentBitmapAsync` (the named O(tier) descent) has **zero callers**; ingest
flat-probes every trunk instead (A11). — `Migrations.Tests` never tests the migrations (A11). — deploy
seed steps swallow failures `|| echo warning`; build invocation forked 4× (I1). — chess provenance
collapse: master PGN + selfplay under one source id, no players, clock skipped (A12). — `vite` dev
proxy omits `/chess` → Chess tab broken under `npm run dev` (I2).

### LOW / INFO

See per-bucket files. Includes: duplicate P/Invoke bindings, missing `[StructLayout]`, unchecked
native index accessors, GUC string interpolated into SQL (SUSET-gated), A* heuristic hardcoded to 0,
dead `update_state`, geom empty `#include`d stub SQL (S³ opclass absent), detoast leaks, hardcoded
dev creds (dev-sandbox per repo), unauthenticated `X-Laplace-Tenant` (low-pri auth surface but a real
cross-tenant path — A2), and assorted stale comments.

---

## 4. Fake / hollow / false-green tests & gates (called out separately — these *hide* the rest)

- `decomposer-gates.json`: `min:1` floors on multi-million-row sources (ConceptNet, Atomic2020) — the
  proof gate passes on a single attestation; consensus gate is global per relation type, not
  source-scoped (one source passes another's gate). (S2, verified against `decomposer-gate-check.py:137`.)
- `decomposer-gate-check.py`: `LAPLACE_GATE_ALLOW_HEALTH_TIER=1` forces the tier-law health check to
  pass. (S1.)
- Data-gated unit tests `if (!File.Exists) return;` → PASS with zero assertions; `LanguageReferenceTests`
  hardcodes `/vault/Data/ISO639` so all six permanently no-op on Windows; CILI-gated tests false-green
  without the vault. (A5/A6/A7.)
- `SyntheticDecomposerTests` asserts `LayerOrderingViolationException` that has **zero throw sites** —
  the test cannot pass; `test_arch_template.cpp` asserts `==nullptr` against code that returns non-null —
  the engine gtest suite is red or unrun. (A1/E4.)
- `/v1/capabilities` golden JSON pins wrong routing; `GoldenFactory` swaps the real substrate for a
  fake so every inference golden tests only wire-shape. (A3.)
- `model-synthesize-ci.sh` proves synthesis only by `GGUF > 50 MB`; `foundry-probe.py` (the real
  fidelity check) is never run in CI. `laplace-bench.sql` asserts nothing. `test-all.cmd` swallows a
  `verify-fk.sql` failure then prints "ALL TEST LAYERS PASSED". (S1/S2.)
- **Zero tests** for the CILI backbone decomposer and for the live ConceptNet witness emission. (A6.)

## 5. Stubs / NotImplemented sold as working

Audio, Image (`yield break`); `feature_extractor` (stub bound into interop); `arch_template` (no
dispatch); `TabularDecomposer` (one-dataset hardcode); `model-bench` CLI (no-op returns success);
3 unwired QK-pair implementations (test-only fork); dead degenerate-weight materializer; dense kNN
eigenmaps (test-only); `CompletionsAsync`/`laplace.completions` (dead, never called by any endpoint).

## 6. Claude-authored docs that are wrong or actively harmful (audited as artifacts, per §0)

- `SQL_SURFACE.md`: declares `sql/inference/` "all DEAD, a trap, ships nothing — **delete it**." It is
  **live** (`#include`d by `20_converse.sql.in:111-115,469`) and IS the synonyms/translations/
  translate_to surface. The doc would have a maintainer delete working code. Also: "structural_neighbors
  semantically random" disparages the *intended* form-not-meaning behavior; "eff_mu redundant" contradicts
  a deliberate SQL↔C parity guard. (X3.)
- `AUDIT-CONSOLIDATED.md` + `AUDIT-DECOMPOSERS.md`: headline 3 "CRITICAL" substrate bugs (JSON merkle
  divergence, FrameNet "Subframe of" inversion, UD XPOS dropped) — **all already fixed in code**
  (`grammar_compose.cpp:240-247`, `FrameNetDecomposer.cs:276-279`, `UDDecomposer.cs:52/307-319`). Stale
  to-do lists masquerading as current critical state. (I5.)
- `DYNAMIC_VOCAB_AUDIT.md` / `FOUNDRY_SYNTHESIS_FINDINGS.md`: describe rank-drift / band-ceiling debt
  that is **already paid** (`WitnessConstants.cs:11-23`, `FoundryCommands.cs:530`). (I3.)
- `CLAUDE.md` §0 states the `AGENT_*_REPORT.md` files are "deleted" — they are **present at repo root**.
  The contract makes a false statement about the tree. (I5.)
- `parity-fail.log` is misnamed — it records a *passing* run. Bench CSVs/logs record crashes
  (0xC0000005, 0xE0434352), not citable throughput. (I5.)
- *Recommendation:* delete the root `AUDIT-*.md`, `AGENT_*_REPORT.md`, loose bench CSVs/logs, and
  `.ingest-proof/`; strip the `DEAD/broken/noisy/semantically-random` static tags from `SQL_SURFACE.md`/
  `FUNCTION_CATALOG.md`/`laplace-bench.sql`. (Offered, not done — see §8.)

## 7. What is genuinely RIGHT (so this audit doesn't read as disparagement)

The native core (identity, tier, trajectory, hilbert, glicko2, merkle dedup, NFC, dynamics GEMM/
eigenmaps/procrustes), the geometry extension, the GGUF/safetensors writer (byte-correct), the C#
P/Invoke wrappers (thin, pinned, lifetime-safe), the `NpgsqlSubstrateWriter` bulk-COPY+single-SPI
path, the top-down O(tier) containment probe (`content_descent_bitmap`/`11_entities_exist_bitmap`),
Wiktionary's correct 9 GB streaming, synset/sense instance anchoring on real ILI strings, the
GPU-free no-external-model API, the two-level embedding split, the perft-gated chess movegen, and a
large body of *real* engine/decomposer tests — are all correctly built and verified. The invention's
foundation is sound; the findings above are drift on top of it, not rot in it.

## 8. Scope honesty — what I did NOT do

- I did **not** run a build, the test suites, or a fresh ingest this session. Test *realness* and
  movegen perft *design* were read and judged; their green/red status is **unverified by execution**
  (and §4 shows at least two suites that cannot currently be green). A measured run is the next step.
- Native struct layouts vs C headers were cross-checked only where both sides sat in one bucket;
  `AttestationAggregatedCellNative` padding rests on the layout test passing, not a header diff.
- The two >256 KB rotating logs and the multi-hundred-thousand-line generated parser tables were
  grepped/paged end-to-end, not read line-by-line; disclosed in E2/I4.
- I made **no code changes.** Per the repo rules I did not commit, did not touch the manifest (the S1
  reader restored the file its test-run of the broken codegen had clobbered), and left the cleanup
  deletions in §6 as recommendations for you to approve.

Per-file detail: `per-bucket/<BUCKET>.md`. Coverage manifest: `coverage-manifest.txt`.
