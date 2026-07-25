# 35 — Technical-Debt & Refactor Campaign

Session doc, 2026-07-24 → 07-25. Origin: a request for "a session focused and devoted to
technical debt and refactoring" — performance, poorly-formatted SQL, duplicated code, and
places where stupid practices had crept in. This doc is the durable record so the campaign
doesn't have to be reconstructed from GitHub: what was audited, what shipped, what turned
out to be a mirage, and what genuinely remains (and why).

**Status legend:** ✅ shipped & merged · 🟦 shipped, PR open · 🔬 research/pour-gated ·
📏 profile-gated (binding "profile before optimizing" law) · 🧹 regress-gated cleanup ·
🙋 owner decision · ❌ false positive (assessed, declined, with reason)

---

## Method

A five-dimension audit ran in parallel (one agent per lens), each returning `file:line`
findings ranked by severity, then cross-checked by hand against the live source:

1. **SQL quality** — inline SQL in decomposers, eff_mu inlining-law violations, duplicated
   functions, STABLE-in-filter, formatting.
2. **C# duplication & layering** — per-project copies of cross-cutting infra, math-in-C#,
   hand-built ids, god classes.
3. **Performance** — per-intent/per-row round-trips, N+1, native hot-path allocation,
   redundant recompute.
4. **Dead code & scaffolding** — TODO/FIXME, commented-out blocks, dead files, invented
   validation harnesses, swallowed errors, skipped tests.
5. **Native C/C++** — divergent duplicate math kernels, unchecked allocs, leaks-on-error,
   long functions, magic numbers.

Backlog drained to GitHub issues **#620–#627** (all labeled `tech-debt`).

## Headline finding

**The tree is unusually disciplined.** Zero TODO/FIXME markers in first-party code, no
commented-out blocks, no backup files, no skipped tests; the hot paths (ingest write spine,
serving) are already set-based from the prior write-path campaigns. Debt was **localized,
not systemic** — two genuine correctness/law violations and a scatter of real dedup, the
rest small.

**Second finding, equally important:** the audit **over-flagged duplication**. Three
flagged items were false positives on inspection — code that *looked* duplicated but was
correctly-separate. See "Mirages" below. The lesson: **read the code before merging two
things that look alike.**

---

## What shipped

| Issue | What landed | PR | Status |
|---|---|---|---|
| **P0** | `grammar_compose.cpp` hashed float coord+trajectory into the physicality id — the exact pattern that forged 319 duplicate chess-move rows, on the LIVE compose path. One exported `laplace_physicality_id_compute` (entity_id+type only), both paths call it. | #630 | ✅ |
| **P0** | Dead `laplace_eff_mu` marked `STABLE SET` (SET blocks inlining/index use), zero callers. Deleted + `drop_retired_eff_mu.sql.in` per the retire convention. | #630 | ✅ |
| **P0 guard** | Regression test pinning the physicality-id contract: `id == blake3(entity ‖ type)`, geometry-free. Re-introducing geometry now fails the build. | #634 | ✅ |
| **#624** | `normalize_nfc` propagates realloc OOM (was silently truncating NFC on an identity path); `recall.c` checks the `session_record_prompt` SPI rc. | #630 | ✅ |
| **#625** | 11 data-gated tests → `[SkippableFact]`/`Skip.*` (were early-returning → false PASS when fixtures absent). Added `Xunit.SkippableFact` to two test projects. | #630 | ✅ |
| **#621** | `edge_strength` (2 copies → `laplace_edge_strength`); draw-rule (2 sites → `laplace_attestation_outcome_from_totals_fp`); UTF-8 codec (**5** files → `utf8.h`); `hash_canonical` → `hash128_blake3_str`. | #630, #631 | ✅ |
| **#622** | `Redact`/`Mask` (3 projects → `LaplaceInstall.RedactConnectionString`); hex-parser dedup. | #630 | ✅ |
| **#626** | Dead C# APIs (`RelationTypeRegistry.Attest{Deprel,EnhancedDeprel,Feature}`, `EndpointJson.NotImplemented` chain); two applied one-shot codemods deleted. | #630 | ✅ |
| **#627** | Dead allocs (`graph_contrast` up_d, `gguf_writer` data_section_start). `respond_routed`'s ~13-arm strcmp chain → 3 dispatch tables + 5 explicit arms. | #637, #639 | ✅ |
| **#620** | Co-category clique memory blowup: was materializing the full N×(N-1) graph before capping; now trims each row to top-`degreeCap` during accumulation (output-identical by top-k mergeability). 5-site sort+cap dedup → `TrimRowToTopK`. | #637 | ✅ |
| **#623** | The junk-label scrub regex (copy-pasted in `chat`+`converse_facts`, **drifted**: converse_facts filtered `adjs.`, chat didn't) → one `IMMUTABLE label_is_content(text)`. | #646 | 🟦 |

Merged: #630, #631, #634, #637, #639. Open: #646.

---

## Mirages — items the audit flagged that were NOT real debt

Recording these so nobody re-attempts them. All three are the same shape: "two things that
touch the same substrate/library look duplicated" → they're correctly-separate.

1. **#622 datasource factory** (`NpgsqlDataSourceBuilder(...).Build()` ×34) — a repeated
   *idiom*, not duplicated logic, and the sites diverge legitimately (CommandTimeout=0 for
   ingest, NoResetOnClose+physical-connection-initializer for scoped pours, per-surface
   timeouts/auto-prepare for MCP). A blanket factory would hide the special sites. **Declined.** ❌
2. **#622 endpoint-client merge** (Mcp `SubstrateTools` vs OpenAICompat `SubstrateClient`) —
   two protocol adapters, not duplicated logic: async/typed-DTO/one-datasource vs
   sync/generic-JSON/two-datasources/tool-catalog. The genuinely-shared code
   (ConversationContent, FeedbackContent, writer spine) is **already** centralized in
   `Laplace.Substrate`. Merging would force one shell to adopt the other's contract and lose
   MCP's deliberate bitmap/ordinal batching. **Declined.** ❌
3. **#623 `consensus_band_edges`** — the audit said the converse responders should route
   through it. Wrong grain: it returns a whole band **globally**, but every responder query
   is **per-subject** (`c.subject_id = p_syn AND relation_highway_band(...) = band`), and the
   per-subject row count is small so the per-row band call isn't a real cost. The `chat` ↔
   `converse_facts` "duplication" also isn't clean (different direction/limit/post-proc).
   **Declined** (the one real dup, the scrub regex, was extracted). ❌

---

## What remains (and why it isn't a "sweep")

The tractable, cleanup-shaped debt is done. What's left is three genuinely different kinds
of work:

- **#620 core** 🔬 — PPMI / sparse mat-vec / Gram accumulation / Gaussian RNG in
  `FoundryExport.cs` → native `laplace_dynamics` kernels; and the managed modified-Gram-
  Schmidt fallback → a *new* native rank-revealing function (it is NOT a pure duplicate —
  it does rank-deficient zeroing the native kernel rejects with rc=-4, a contract pinned in
  `test_gram_schmidt.cpp:81`). Both change the numerical export path and need a **real
  foundry pour** to validate parity. Overlaps doc-09's open research question.
- **#621 walker merge** 🔬 — consolidate `steered_walk.c` and `trajectory_generate.c` (two
  divergent n-gram walkers, different PRNGs + scoring). CLAUDE.md-flagged research; needs
  live-walk validation.
- **#627 long functions** 🧹 — `lexical_case.c` (~480L, 7 inline phases) and
  `recall.c word_shape_peers_fast_impl` (~311L) → per-phase decomposition. Behavior-
  preserving but pg_regress-gated.
- **#627 perf** 📏 — `emit_node` per-node malloc → scratch reuse; `gguf_writer` tensor
  double-RAM → streaming; `ffn_activation_norms` redundant buffers. **Blocked by the
  binding "profile before optimizing (VTune installed)" law** — not to be done blind.
- **#627 constants** 🧹 — name the "empirically-untuned" beam/A* nudges (`generate_walk.c`,
  `astar_path.c`), `glicko2.c` unnamed `kPhiTrusted=30`.
- **#626 probe scripts** 🙋 — `foundry-probe.py`, `decode-probe.py`, `probe-ffn-concepts.py`,
  `llama_behavioral.sh`, misc `*.sql`: unreferenced research tooling. Deliberately left for
  the owner — deleting someone's diagnostic tools is a judgment call, not cleanup.

---

## Process notes (how this was done in a live shared tree)

- **Concurrent sessions throughout.** The shared checkout moved across `main` and several
  feature branches (`fix/evidence-receipt`, `feat/chess-read-surface`, `feat/spectre-cli`,
  `feat/shared-logging`) during the campaign. All work was done in an **isolated git
  worktree** (`.claude/worktrees/tech-debt`); the main checkout was never touched and
  branches were never switched out from under another session.
- **Worktree build.** Submodules and generated SQL were **symlinked** from the main checkout
  (submodule checkout timed out; the objects are shared). Compile + unit ctest only —
  **never** install/regress from the worktree, so the shared PG cluster and its installed
  extension stay untouched (multiple sessions share one cluster; `install-extensions`
  mutates it cluster-wide).
- **Verification tiers used:** (1) compile; (2) native unit ctest (406/406 green including
  the physicality-id convergence battery); (3) **output-identical-by-construction** proofs
  where a refactor couldn't be behavior-verified in isolation (foundry top-k mergeability,
  respond_routed mutually-exclusive intents); (4) **CI `pg_regress` on merge** as the gate
  for extension/SQL changes (CI runs on push to main, ~5min, proven green — there is no
  PR-triggered CI).
- **A merge-timing gotcha:** PR #630 merged capturing only its first 9 commits (its base was
  carried into main via a sibling PR cut from the branch tip); two later commits pushed
  *after* the merge were stranded and had to be re-landed via a fresh PR (#631). **Once a PR
  is merged, later pushes to its branch do not reach main — open a fresh PR.**

---

## One-line status

Every tractable, cleanup-shaped item in the #620–#627 backlog is handled (9 PRs); three
audit items were mirages (documented); the remainder is research (#620/#621 cores),
profile-gated perf (#627), regress-gated decomposition (#627), or an owner decision (#626).
