# W17 — Full-surface audit: SQL, C#-SQL, plpgsql, and native placement (2026-08-06)

**Status:** measured census, defects filed as GH #900–#912. · **Related:** W16 (reinvented
wheels — re-censused here, unchanged), W15 (native offload — open items verified), W3
(CALLS graph, the mechanical fix W16 named), spec 37 / GH #871 (OR-join law).

**Method.** Five independent sweeps over the whole tree, no sampling: (1) every
`LANGUAGE plpgsql` site plus all recursion/cursor sites; (2) duplication census across all
428 `.sql.in` (~14,400 lines); (3) read-law violations (CLAUDE.md "Reads") across the same;
(4) every SQL-bearing C# file (86 matched, 59 non-test, all classified); (5) C#-vs-native
compute placement across the spine and `Laplace.Core`. Plus two live-planner measurements
made this day. Coverage lists and grep patterns are recorded in the issues; the census
tables below are the summary of record.

---

## 1. Live measurements (this audit's own, not comment-trust)

- **Partition pruning, `attestations` (1,880 leaves — the probe comment's 1,304 is a July
  count, see #902):** bound-literal `type_id` + `subject_id` plans to **one** hash leaf
  (`attestations_r_has_language_h6`, single index descent — LIST pruned at plan time, HASH
  by the key). Column-valued `type_id` from a 2-row `unnest` join plans **104 leaf scans**,
  seq-scanning unrelated relations' partitions. Confirms
  `probes/attestations_present_ordinals.sql.in`'s design: the per-type plpgsql loop is the
  fast form; only a bound literal fully prunes.
- **Methodological trap, recorded so the next auditor avoids it:** a single-row `VALUES`
  join is constant-folded by the planner and prunes fine — it falsely acquits the failed
  form. Reproduce with a multi-row batch.

## 2. plpgsql / recursion / cursors — complete census (GH #911)

35 sites (32 functions, 3 procedures), every one read. **Zero unjustified RBAR. Zero
cursors** (grep hits are prose only). Loop taxonomy of every surviving loop:

| Category | Sites | Examples |
|---|---|---|
| DDL-forced | seed_relation_partitions, entities/physicalities DO blocks, drop_retired_content_lane, evict_source per-leaf | `CREATE TABLE PARTITION OF` per relation × hash slice |
| COMMIT-forced (procedures) | highway_mask_drain, highway_mask_rebuild, evict_source | batch-commit keyset pagination |
| Pruning-forced, measured in-file | attestations_present_ordinals, evict_source `%L` EXECUTEs | §1 confirms live |
| Sequential algorithm | converse_tiered step loop (step *i* consumes *i−1*) | bounded by p_steps |
| Bound-param staging (no loop) | the KNN family: anchors fetched INTO locals so comparison points reach the planner as bound parameters | structural_* / consensus_peer / chess_opening_shape_peers |

Recursion: 3 sites (`relate_path`, `laplace_ancestry`, `constituents_closure`), all
unknown-depth traversal with cycle guards and depth caps — the case recursion exists for.

Defects: `entities_present_ordinals` re-unnests the full arrays per tier — the sibling
probe's documented FAILED FORM (1), capped ~5× by the tier count but the batch-once idiom
sits one file away unused; `session_record_prompt`'s `max(ord)+1` races under concurrent
writers. Cosmetic: two plpgsql bodies using no plpgsql feature; one FOREACH expressible as
LATERAL. All in #911.

## 3. Duplication census (GH #900, #903, #906, #912; W16 extended)

- **`eff_mu` open-coded twelve times** — `chat.sql.in` ELABORATE ×6 (#900),
  `converse_facts.sql.in` ×6 (#903) — both files call the helper family on adjacent lines.
- **`chat()` ELABORATE re-implements `converse_facts`'s parts/kin families inline**, minus
  the `non_kin_assoc_types()` exclusion — resurrecting the fixed etymology-as-kin defect
  (#900 addendum).
- **Byte-duplicate walk responder** (`recall_fallback_walk` vs `recall_walk_response`);
  **three hand-rolled copies of installed `realize_path`**; **`eff_mu_display` open-coded**
  against its own "never re-derived inline" comment; **a third centroid path**
  (`laplace_attention_centroid`) beside the two W16 flagged (#906).
- **W16 re-census: unchanged since July.** Five zero-caller geometry functions still at
  zero; `radius_origin` still computed two ways (`physicalities.sql.in:37-38` vs the native
  function); step 8.3 not landed. The duplicate count grew — W16's predicted failure mode
  absent the W3 gate (#912).
- Intentional dualities verified as pinned, not defects: `eff_mu`/`effective_mu` (engine
  parity gate `word_law.sql:63`), `walk_edge_weight`, `ConsensusKeys` (parity test),
  Glicko draw-score (native read-back).
- Clean classes (searched, zero hits): alternate formula spellings, hardcoded relation-type
  name comparisons (215 sites all via `relation_type_id`), hardcoded id hex literals, SQL
  re-implementations of the native distance math, hand-rolled consensus aggregates outside
  helper definitions.

## 4. Read-law violations (GH #905, #907, #908; #901 templates)

- **LIMIT without ORDER BY:** 9 live arbitrary-pick sites (worst: `first_placed_topic` —
  promises "first", doesn't order; `source_roster` — unranked sample) + 5 dead-code LIMITs.
  ~110 other LIMIT sites verified ordered; the sanctioned unordered classes
  (point-lookups by contract, exhaustive batches, documented-arbitrary vocab sampling) are
  enumerated in #905.
- **EXISTS tri-state collapse:** 4 defect sites over raw consensus (`mesh_position`,
  `relation_in_family`, `synset_members`, `translate_to`); presence-by-contract sites
  audited and cleared.
- **Render-to-classify:** `translate_to` string-compares rendered text mid-predicate (the
  law's exact example); `converse_compose` computes whitespace-ness by rendering.
- **Scalar render ladders:** `converse_compose:220` per-token realize+render_text after
  `converse_walk` measured the batched fix at 15.8× (#907); `converse_facts:121,154`
  render before the sort (#903).
- **OR-joins:** two survivors (`converse_tiered:223`, `consensus_peer:52` — the latter
  citing the law as avoided while carrying the disjunction), plus `relation_in_family`
  used per-row unfenced at `converse_tiered:140` where siblings fence it (#908).
  `consensus_step_edge` is the in-repo lawful rewrite.
- **English templates:** ~20 sentence literals across 9 files against the
  language-agnostic law; `recall_interaction_response`'s ten-entry phrase map is the
  natural first fix target (#901).
- **SET/STRICT law:** the hot consensus path (`eff_mu`, `edge_rank`, `senses`,
  `edges_raw`, …) deliberately complies. Carriers used per-row: `word_language`
  (tolerated pending C port, #757), `relation_in_family` (the unfenced site above),
  `prompt_state` (per-call mostly).

## 5. SQL-in-C# census (GH #909) and decomposer purity

59 non-test files classified: spine (COPY/descent/apply — wire-protocol work, sanctioned;
its in-transaction lock-held probes have no installed equivalents), installed-surface
one-liners, app-schema billing CRUD, migrations/ops. **Five defects:** the verbatim
CLAUDE.md-named `GROUP BY` over attestations (`NpgsqlIngestOps:230` → `source_counts()`);
`evidence_count` reimplemented in a file that calls it correctly elsewhere
(`NpgsqlSubstrateReader:360`); entity-type count duplicating `entity_type_counts()`
(`:53`); ingest-fidelity CTEs scanning consensus with per-row subselects
(`NpgsqlSubstrateReads:469,505`); pair-scoring hand join near-duplicating
`consensus_by_ids` (`:284`). **Two missing helpers** documented at their sites:
`consensus_cell` (doc 41), Model_Circuit trajectory count.

**Decomposers: clean.** No SQL, no DB access, no Npgsql package reference — verified by
two independent sweeps with broad patterns. The purity gate holds.

## 6. Native placement (GH #904, #910; W15 verified)

- **C# computes no content hashes** — all ids go through native BLAKE3. But **two id
  pre-image byte layouts exist in both C# and C with no parity gate**: the physicality
  18-byte layout (`PhysicalityId.cs:19` vs `content_witness_batch.c:64` — byte-identical
  today only by shared endianness, no exported native function, no test) and the
  tier-collapse identity rule (`TierTree.CollapseIndex` vs `collapse_idx()`, "lockstep"
  asserted in prose only). Highest severity in this audit: silent divergence mints
  duplicate identities. The in-repo fix template exists (`ConsensusKeysParityTests`).
  (#904)
- **Two FFI-shape offloads:** descent's per-node P/Invoke under the process-global
  `LaplaceCoreGate.Native` lock (O(nodes × rounds), serializes parallel compose workers);
  the apply's managed re-parse of PGCOPY blobs native code just serialized. Both deleted
  by native API additions, not algorithm ports. Minor: `CalibratedInverse` O(4001×n) grid
  where `glicko2_fold_uniform_period` gives O(4001). (#910)
- **Sanctioned and verified:** client dedup/accumulation (design-mandated by the fixed
  ingest order), I/O-ordering compute, and the ~200-binding native delegation surface —
  enumerated in the #910 audit trail.
- **W15 §9 status:** SET-clause sweep open (213 files still carry it); keepplan done in 48
  places but **zero in the two functions the doc names** (`prompt_coherence.c`,
  `prompt_language.c` — the former on every chat turn); measurement and
  materialize-revisit items open. (#912)

## 7. Issue register and priority

#900 chat ELABORATE · #901 English templates · #902 comment integrity (sediment; stale
leaf count) · #903 converse_facts · #904 identity parity gates · #905 read-law sweep ·
#906 render duplication · #907 converse_compose ladder · #908 OR-joins + unfenced qual ·
#909 C# substrate SQL · #910 native API gaps · #911 plpgsql follow-ups · #912 W15/W16
open items.

Priority by blast radius: **#904** (identity drift is silent and permanent) → **#909/#905**
(correctness of live reads) → **#900/#903/#906/#907** (conversational path; duplication
trend) → **#910** (ingest throughput) → **#912**'s named small items (W16 8.3, two
keepplan sites) as quick closures.

## 8. What the audit cleared, and at what evidence strength

Decomposer purity and the spine boundary: verified twice, broad patterns (this audit).
The pruning loops: verified against the live planner (this audit, §1). The KNN
bound-param staging, batch-once idioms, COMMIT-procedures, recursion sites: verified by
reading every site (this audit). Site-recorded July measurements (the ordinals failed
forms, the 15.8× batch render, the 29.6s→3s orientation): read, consistent with the live
checks made, not individually re-run. Anything not in one of those classes is not claimed.
