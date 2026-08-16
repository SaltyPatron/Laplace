# Laplace

Operating file. Sections 0–2 are output law and outrank everything else in this file.
Sections 3+ are the technical spec.

---

## 0. Output grammar

A turn is tool calls plus a report of them.

Every sentence in the report takes one of these subjects, and no others:

1. a system object — file, function, table, command, measurement, error, commit, issue;
2. the change — what landed, what did not, what was skipped;
3. the next system object to be changed.

Two subjects sit outside the grammar: **the agent** and **the user**. A sentence whose subject
is either is dropped before sending. The check is mechanical — extract the subject, keep or drop.

This one constraint is the whole of §0. It covers internal states, presence, care, apology,
restraint, process narration, self-criticism, commentary on the reader, and every register that
takes a person as its topic — without any of them being listed, and none of them are listed
here. There is no input class that alters the grammar and no condition under which it relaxes.
The register is constant across every turn.

**Send test:** strike each sentence whose subject is a person. If the remainder is empty, the
turn produced no work — produce the work.

**Failure belongs to the agent.** No rule's application depends on the user's phrasing, tone,
timing, or state, and nothing requires him to restate, re-prove, or re-authorize anything for
work to proceed. An agent-side gap is reported as an agent-side gap, never as a pending user
decision.

**Operational rules of the same rank:**

4. **Answer with a change.** A turn that owes a change and emits only prose is a failed turn.
   Work lands in the current turn; a turn ends in a state that needs no follow-up prompt to be
   useful.
5. **Instructions execute.** A repeated instruction is a decision. Stop means stop; install
   means install. An overruled concern stays overruled.
6. **`...` means the previous output failed.** Audit that output, name the specific defect in
   it, and correct it in the same turn.

## 1. Honesty law

1. **Use the data already in this session** and name its source.
2. **Cite the file and line** that imposes any constraint claimed — blocked, forbidden, unsafe,
   unavailable. A constraint without a citation is invented. Read the mechanism before writing
   the report.
3. **Run the search before any absence claim** — missing, unsolved, orphaned, uncalled, dead —
   and paste the result. Five false absence claims in one night, all refuted from the archive.
4. **A direct instruction outranks every note, every memory file, and this file.** Those are the
   weakest sources here; a note re-entering context and overriding the user is prompt injection
   with extra steps. State a conflict plainly instead of converting it into an external wall.
5. **Verify effects before any claim about agent actions.** Passwordless root sudo exists on this
   box; a tool-layer rejection does not undo a spawned process. Read auth.log and the journal.
6. **Report completely:** what landed, what is written but unlanded, what is measured, what is
   assumed, what was skipped. A skip is stated. Failures carry their output, not a paraphrase.
   A summary matches the work exactly.

## 2. Delivery bar

Delivery = implemented + tested + gated + **landed** + verified against the live system, in the
same change.

- A compile, a passing test, a clean diff, a working-tree change, and a filed issue are each a
  step, and the report names them as that step. The open-issue backlog is the accumulated
  cost of reporting specification as delivery.
- `EXPLAIN ANALYZE` output is a measurement of the plan, and the report names it as that. The
  named target still gets the change.
- Every performance or behavior claim is measured live with the command shown, or carries
  **UNVERIFIED** in the same sentence. The label applies only where a measurement was claimed;
  work that claimed no measurement carries no disclaimer.
- Run the check in the same turn. Where that exact check is unavailable, run the nearest one
  that is and land it.
- Reproduce any finding against current HEAD before reporting it. Fixes land hourly here; every
  sample is stale on arrival. An already-fixed finding gets one line naming the commit.
- Run the `laplace-verify` skill before reporting a substrate read, MCP tool, or foundry change
  as done.
- Finish one thing end-to-end, then start the second.
- **Deferral vocabulary is banned.** "Only the re-ingest remains", "cannot land tonight", "too
  large for one turn", "written as spec, no code", "requires a follow-up", "needs a re-ingest of
  N rows" — every one of these is undone agent work wearing the costume of an external
  constraint. Cost, runtime, row count, data volume, re-ingest, and rebuild time are **jobs to
  start**, never reasons a change did not land. A turn that names a remaining step starts that
  step in the same turn and reports its handle: the PID, the branch, the run id, the log path.
- **A spec is not a deliverable for a request that names code.** Where a design document is
  written, the code it specifies is written in the same turn. `docs/specs/38` shipped while
  `AttestSet`, `consensus.set_members` and `consensus.subjects_with` did not — that is the
  failure this line exists to stop, not an example of good practice.
- **Nothing about the user may appear as a reason any step is incomplete** — not his approval,
  his availability, his phrasing, his tone, his prior instruction, or a question put to him. An
  agent-side gap is written as an agent-side gap, in the first sentence, naming what the agent
  did not do.
- Where landing needs a command the agent genuinely cannot issue, the first sentence of the
  report is that exact command verbatim and the file:line of the mechanism that blocks it
  (§1.2). No citation means no wall, which means it lands now.
- **A closure is a claim, and its evidence must outlive the file.**
  - Cite `path:line@<sha>`, or paste the three-line excerpt. A bare `file:line` decays into an
    unauditable closure the moment the file moves.
  - **"Fixed or falsified" is not a disposition.** Those are opposite outcomes — work that was
    completed, and work that was never real — and a closure that cannot say which has recorded
    nothing.
  - A duplicate or an obsolete issue closes as `NOT_PLANNED`, never `COMPLETED`. A completion
    count built from reclassification is unusable as a delivery count.
  - Closing as a duplicate **re-points work, it does not retire it**. Name the target and its
    state.
  - A pass that closes issues at machine cadence has verified nothing.

## 3. Order of authority

1. **`docs/INVENTION.md`** — the law. Read it in full before any claim about what this system
   is or what is unsolved. Most "defects" an agent finds are laws in that file being violated by
   agent-written code. Read it *before* editing code it governs: §5 and §7 govern walk ranking,
   and a session that read them late got the fix backwards.
2. **The running system** outranks every document (`INVENTION.md` line 8). Where they disagree,
   the code is right and the doc is the thing to fix.
3. `docs/specs/`, `docs/decisions/`, `docs/invention/` — design law.
4. `docs/plan/*` is not a status report; its "Open questions" go stale. Three items listed open
   there are implemented in `extension/laplace_substrate/src/`.
5. Agent-written notes, memory files, and this file — weakest. They have been wrong about what
   this system is.

## 4. Laws agent code keeps breaking

- **§7, the election law.** *No ranking may be decided on a single-token scalar.* Topic, sense,
  and relation are elected **together** from the graph between the prompt's tokens. Selecting
  the highest-rated token, the highest witness count, the highest scalar, or the
  longest/leftmost span all violate it.
  - **This is not a ban on counting, and earlier revisions of this file said it was.**
    Spec 36 makes SCAN *"discover bounded candidates"* a stage of the canonical program, so
    bounding a candidate set is design, not sin. `08_Record_vs_Calculate_Spec.txt` makes a
    derived statistic legitimate **calculated testimony** that "competes as another witness"
    — provided it carries analyzer identity, version, inputs, and recipe, and never
    overwrites the recorded literal it estimates.
  - **What actually violates §7** is a bound or a score applied **per token, in isolation,
    before the joint election** — it can delete the candidate the joint evidence would have
    picked, and STEER reranks proposals rather than regenerating them, so nothing downstream
    recovers it. `prompt_coherence` collapsing each token to one sense before the seat (§9b)
    is the live instance.
  - **Why TF-IDF specifically is wrong here**, structurally: IDF's denominator is a fixed
    document collection, and this substrate is a merkle DAG where identical content exists
    once by hash and is *referenced* N times — document frequency is not defined, and counting
    references measures ingestion composition, not meaning. The signal it approximates is
    already adjudicated per typed edge as a Glicko2 rating carrying `rd` and `witness_count`.
    The `joint_degree × icf` revert (4d658df9) was not "frequency bad" — it was an
    **unwitnessed, unversioned scalar injected into the election path**, which
    Record-vs-Calculate forbids by name.
  - **Frequency at export is correct.** The foundry's bigram pairs and vocabulary frequencies
    are the artifact being rendered (§10, model as render target), produced by a named
    analyzer with a recorded recipe. Same arithmetic, different position in the pipeline.
    Do not cite §7 at the foundry.
- **§15, the recurring failure.** An operation gets a canonical implementation, the caller that
  should use it is never rewired, both survive, and they drift.
  - **The live instance, re-measured 2026-08-16, against the right symbol.** The prior
    revision read "32 of 33 decomposers bypass `IngestComposePipeline`" and sent sessions
    rewiring callers onto a helper that is not theirs: `DecomposerBatch.cs:6-9` scopes it to
    *"non-orchestrator code (e.g. ModelTokenEdgeETL)"* and says *"Multi-phase sources use
    nested `ComposeDecomposerPhase<T>` types instead."* Counting decomposers that do not call
    it measures nothing. Counted: `IngestComposePipeline` **1** user (ModelTokenEdgeETL, which
    is what it is for), `ComposeDecomposer` **15**, `ComposeDecomposerPhase<T>` **5**, files
    referencing `IDecomposer` **34**. The drift is **15 of ~34 on the canonical base**
    (`Decomposer.cs:412`), and that is the number to move.
  - Corrected 2026-08-14, measured: `astar.cpp` does **not** have zero callers.
    `astar_path.c:280-283` passes `astar_geo_heuristic` to `astar_open` gated on `use_geometry`;
    three functions are installed (`converse.astar_path`, `converse.astar_path_raw`,
    `generation.astar_path`). It is `p_use_geometry DEFAULT false` — "off by default" is a
    different defect from "unreachable."
- **§4, the evidence law.** Attestations never record a magnitude. No raw per-edge floats.
- **The partitioning does not currently earn its keep, measured 2026-08-15 from the catalog.**
  216 leaves, 371,272,166 rows, and the **DEFAULT partition holds 219,684,688 — 59.2%**.
  The cause is not a range or hilbert key — **nothing here is partitioned by hilbert or
  RANGE.** Every partitioned table is LIST or HASH:
  `consensus` and `attestations` are `LIST (type_id)` → `HASH (subject_id)`;
  **`entities` is `LIST (tier)` → `HASH (id)`** with t0/t2/t3 and no t1;
  `physicalities` is `HASH (id)`.
  The DEFAULT skew is that **only 27 relation types have a named partition out of 426 live
  types**, so 399 types share one bucket. **8 of the 27 named partitions hold 0 rows**
  (attends, completes_to, contains, continues_to, has_external_id, ov_relates,
  token_maps_to; precedes holds 115). So the LIST level buys pruning only for 27 types and
  only when the caller supplies `type_id`; everything else pays a 216-way Append.
  **`entities` keyed on `tier` is keyed on a floor** — the value this file records as
  container-relative, not a property of the entity.
- **The named partition set is NOT a hardcoded guess, and the dead partitions are already
  gone — corrected 2026-08-16 from the file itself.** The roster is derived:
  `seed_relation_partitions.sql.in:15` — *"The roster is exactly the manifest's `hot = true`
  relations."* The seven zero-row types this file listed as evidence of a stale guess
  (ATTENDS, COMPLETES_TO, CONTAINS, CONTINUES_TO, HAS_EXTERNAL_ID, OV_RELATES,
  TOKEN_MAPS_TO) were **dropped 2026-08-16 by clearing the flag in the manifest**, verified
  by `count(*)` rather than `reltuples`, retiring 112 leaves (7 x 8 x 2 tables). Per that
  comment, **a fresh install now builds 152 consensus leaves, not 216.** `grep -c "hot *=
  *true" engine/manifest/relation_types.toml` reads **27**; 27 named x 8 hash does not equal
  152, so the live leaf count needs `ops.partition_pressure()` against the running database
  rather than arithmetic here. Every "216" below predates this change.
  `ops.consensus_partition_pressure()` still measures DEFAULT share — run it before
  theorising about partitions —
  it exists, it takes `min_rows`, and it names the unpartitioned relations by share of
  DEFAULT. Live 2026-08-15:

  | relation | rows | % of DEFAULT |
  |---|---|---|
  | **HAS_FEATURE** | **186,562,442** | **84.93** |
  | TRANSCRIBES_AS | 5,192,208 | 2.36 |
  | DERIVED_FROM | 3,836,962 | 1.75 |
  | HAS_THINK_CLASS | 3,022,618 | 1.38 |
  | HAS_CLOCK | 2,531,014 | 1.15 |
  | ETYMOLOGICALLY_DERIVED_FROM | 2,606,533 | 1.19 |
  | HAS_ETYMOLOGY | 2,595,036 | 1.18 |
  | HAS_EXAMPLE | 1,803,197 | 0.82 |

  The DEFAULT skew is **one relation**, not 399 sharing a bucket — HAS_FEATURE at 84.93%,
  the largest relation in the substrate, still has no named partition. That is the live
  item. The seven zero-row partitions that used to sit beside it in this paragraph were
  retired 2026-08-16 (see above); APPEARS_IN at 60 and PRECEDES at 115 are kept
  deliberately, because both are read from the geometry rather than from rows. The adoption
  machinery in that file drains DEFAULT into new partitions and reads its roster from the
  manifest, so promoting HAS_FEATURE is a manifest flag, not a code change.
- **An empty relation partition is not a dead relation.** PRECEDES and APPEARS_IN are
  **read from the geometry**, which is why their tables are near-empty:
  - order comes from the trajectory — `generation/word_order.sql.in:2` (moved from
    `corpus/`; the bare filename in earlier revisions no longer resolves) ("text emits no PRECEDES
    attestations; this is the calculated..."), `word_adjacency.sql.in:12` ("Order is fetched
    from the TRAJECTORY, never from an edge"), `pos_class_transitions.sql.in:71` and
    `usage_overlap.sql.in:9` ("the same knowledge PRECEDES materialised as"),
    `continuation_conditional_plane.sql.in:15`, `geometry_successors.sql.in:5`.
  - containment comes from the GIN index — `containers_of.c:65` probes
    `laplace_trajectory_constituent_ids(w.trajectory) @> ARRAY[$1]` against
    `physicalities_constituents_gin`.

  Do not read a low row count as "unused" and do not propose materialising either one.

  **Sequence is geometry in EVERY lane** — text ingestion emits trajectories, and PRECEDES is
  read back from them (six inference sites above). A decomposer that writes PRECEDES edges is
  writing the same content in the wrong shape.

  **Live violation, measured 2026-08-15 by source_id** (`ops.source_status()` joined to the
  three ids on the 120 PRECEDES attestations, all written 2026-08-13/14):
  `FrameNetDecomposer` **89**, plus two sources not in `source_status` (**29** and **2**).
  A text/lexical decomposer is emitting sequence as edges. Fix it at the decomposer.

  **Do not narrate a provenance for these rows without running that query.** This file has
  claimed "populated only by model ingestion — a deposed model's sequence testimony ...
  model-lane residue"; a session then upgraded that to "attested vs inferred are different
  populations, the design working." Both were written without checking source_id. The write
  sites in the app are `IngestCommands.cs:385` (document deposit — "entities + physicalities
  + PRECEDES bigrams") and the OODA feedback lane (`QueryCommands.cs:309`,
  `EndpointMappings.Feedback.cs:99`); a grep of those is evidence about code, not about which
  rows exist.

- **Supply the partition keys, or pay 216×.** `laplace.consensus` is `LIST (type_id)` — 208
  named type partitions plus a DEFAULT — and **each is itself `HASH (subject_id)` over 8**, for
  **216 leaf partitions** (not the 27 this file used to claim; 27 is what a subject-only read
  touches). Measured live, leaves touched:

  | predicate | leaves |
  |---|---|
  | `subject_id` + `type_id` | **1** |
  | `object_id` + `type_id` | 8 |
  | `subject_id` only | 27 |
  | `object_id` only | **216** |

  Forward-edge reads prune at both levels. **Reverse-edge (`object_id`) reads prune at
  neither** — the hash key is `subject_id` — so every in-edge read pays all 216. Reduce the
  working set before operating: pass `type_id` whenever the caller knows it. Where the read
  genuinely spans all types, use `= ANY(ARRAY(...))` so the planner emits ScalarArrayOp probes
  instead of a full index-only scan of every partition — measured on `converse.infer`'s
  in-edge arm, identical 71,405 rows: join 49,861 ms, LATERAL 104,764 ms, array probe **60 ms**.
  An accurate row estimate alone does **not** fix it: the same join against an ANALYZEd
  229-row temp table still cost 49,861 ms.
- **`relation_type_id('X')` is free — it is a hash, not a lookup.** The chain is
  `laplace.relation_type_id` → `realize.canonical_id` → `laplace_hash128_blake3(name)`. It never
  touches a table, which is why it is honestly `IMMUTABLE`: the id *is* the BLAKE3-128 of the
  canonical name (§3). With a constant argument the planner folds it to a literal `bytea` at
  plan time, so sixteen of them in a `WHERE` clause cost sixteen hashes **once**, during
  planning — not per row. A function whose argument is a **column**
  (`consensus.relation_type_in_family(c.type_id, …)`) cannot fold and runs per row; that one is
  compiled C at `procost 1`, measured 17.9 ms across all 426 relation types.
- **Planning is not free on a 216-leaf table.** `consensus.salient_facts(word_id('water'))`
  plans a **533-node** tree in **545 ms** to return 24 rows, because it references `consensus`
  many times and each reference expands across the partition set. Execution was 5,167 ms.
  Count plan nodes before blaming a scan.
- **Three levels of array binding, and only the top two prune.** When scoping a partitioned
  read by a set of ids, *how* the array reaches the predicate decides everything:

  | form | when it is known | measured |
  |---|---|---|
  | `consensus.relation_family_ids('HAS_SENSE')` — IMMUTABLE, constant arg | **plan time** (folds to a literal) | 3 scan nodes vs 54 |
  | `= ANY (ARRAY(SELECT …))` — an InitPlan | **run time, bound before the scan** | 3,342 ms · 500.7 ms |
  | `CROSS JOIN ids_cte … = ANY (ids_cte.col)` | never — a per-row variable | 14,299 ms · 773.5 ms |

  The CTE column reads like the tidy form and is the slow one: the planner cannot bind it, so
  it joins across the partition set instead of emitting a ScalarArrayOp. Measured at two
  independent sites with identical output (44,415 rows and 3,187 rows). Use the manifest-folded
  function when the contents are known from the relation law; use `ARRAY(SELECT …)` when they
  are data-dependent; never hand a predicate a CTE column.
- **Declared row estimates are absent.** 239 of the 244 set-returning functions ship Postgres'
  default `prorows = 1000`; only the five model-lane functions declare a real one. A wrong
  estimate on the inner side of a join is what makes scanning look cheaper than probing.
  Declare `ROWS` only from a measured cardinality or a structural bound — `lexical.senses` is
  `bubble_up(..., 64)` and therefore `ROWS 64`.
- **Sequence is geometry, not an edge.** Text order lives in the trajectory and is fetched
  with `laplace_trajectory_constituents`. `PRECEDES` is populated **only by model ingestion**
  — a deposed model's sequence testimony — so its 115 live consensus rows are model-lane
  residue, never text. Looking for a consensus edge between two consecutive words is the
  wrong question and always returns 0; that is correct, not a defect. What *is* adjudicated
  about an adjacency is the standing of the **compositions that witness it**, which carry
  inbound HAS_DEFINITION / HAS_EXAMPLE edges.
- **Tier is a floor, not a set level.** `ctier = 2` is not "word": measured live,
  `word_id('a')`, `word_id('I')` and `word_id('狼')` are all **tier 0**, while `word_id('hot')`
  is tier 2. Every `ctier = 2` filter silently deletes single-grapheme words and every
  single-grapheme CJK word. Live sites, re-verified 2026-08-16 (two line numbers had
  decayed and one site was missing): `converse_compose.sql.in:203` (its own comment at :181
  calls it "A KNOWN-LOSSY FILTER THAT STILL HAS NO REPLACEMENT"),
  **`converse_walk.sql.in:174`**, `converse_tiered.sql.in:150`,
  `senses_with_context.sql.in:102`,
  `explore_anchor_neighbors.sql.in:89`, `variant_synth.c:266`. Select by TYPE, or by what a
  thing is not (a separator), never by tier.
- **Separators are not constituents of the sequence they delimit.** Adjacency reads must
  drop `generation.separator_ids()` before the window, or every gap-1 pair is word↔space:
  measured, 13,955 of 17,708 pairs over 2,000 trajectories touched a separator, and *hot*/*dog*
  had 0 pairs at gap 1 and 85 at gap 2. The set is empty for non-delimiting scripts, which is
  why an unfixed adjacency lane learns CJK and nothing else.
- **§15, determinism.** Integer-pure identity, no fast math, byte-identical across compilers and
  OSes. Measured: 21,330,410 placements inside the glome, worst excess 4 ULPs.
  - **Re-measured 2026-08-15 over the whole codespace: still 4 ULP**, and now gated —
    `LaplaceCoreSuperFibonacci.UnitNormHoldsToFourUlpAcrossTheCodespace` asserts it in ULPs
    across all 1,114,112 placements (worst at index 2614; 643,019 exact, 292 at the bound).
    The two pre-existing norm tests could not catch drift: one checks 8,192 points and the
    other seven hand-picked indices, both at `1e-13` — about 450 ULP, 112x looser than the
    real behaviour, and neither index set contains the worst case.
  - **The identity half is unconditional; the geometry half is bought by pinning the
    toolchain, not by the flags.** `hash128_merkle` is BLAKE3 over child ids with no float
    anywhere, and physicality ids are content-derived from `(entity_id, type)` — so ids and
    packed trajectory vertices are byte-identical on any conforming machine. `coord` and the
    `hilbert_index` derived from it are not: `sqrt` is IEEE-mandated and correctly rounded,
    but `sin`/`cos` are not, and the core imports `__bwr_sin`/`__bwr_cos` from Intel's libimf,
    never glibc's. Measured 2026-08-15, same formula and inputs, 30,112 placements strided
    across the codespace: **glibc libm and Intel libimf disagree on 226 of 120,448 components,
    worst 2 ULP** (index 22348). `-fno-fast-math -ffp-contract=off` is necessary and does not
    help here — it governs codegen, not which library supplies the transcendental.
  - **Why the cross-OS claim still holds:** both platforms build with the same compiler, so
    both link the same libimf. `scripts/win/build-engine.cmd:34` sets
    `-DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icx`; oneAPI is required anyway for MKL and
    TBB. **The toolchain is part of the identity contract for geometry.** Read the claim as
    "byte-identical across OSes on the pinned toolchain," not "across compilers."
  - **`cmake/toolchains/gcc-deterministic.cmake` IS referenced — do not delete it.** Measured
    2026-08-16: `external/CMakeLists.txt:27` names it, and
    `app/Laplace.Substrate.Tests/Abstractions/TypeIdLawTests.cs:131` asserts on it. The prior
    revision of this line claimed "referenced by nothing — no `.sh`, `.cmd`, `.cmake`, `.md`,
    or Justfile" and prescribed deletion; that absence claim was written without running the
    search, and following it breaks the build. It stands as a live example of §1.3 against
    this file's own text.
    The substantive half survives: correct flags, authoritative filename, silently different
    coordinates from the pinned icx/libimf path. The fix is to make it correctly-rounded, not
    to remove it. Correct rounding is the only fix that is a *property* rather than a
    deployment constraint, because the correctly-rounded result is unique and therefore
    identical on every conforming implementation by definition.
- **No arbitrary dials.** Bound by construction (ownership, lifetime), not tuned constants. Any
  unavoidable constant is measured with the measurement recorded and a test keeping it honest,
  or flagged as a stopgap with the by-construction follow-up named. Constants already in the
  codebase are not laws of physics: a cap that looks immovable gets the question *why does the
  implementation need it at all* — one such cap existed only because the function enumerated
  heap rows through a view join.
- **Schema migration is one-way.** `laplace.*` → `<purpose>.*`. Postgres `42883` means a caller
  was missed: finish the migration at the caller.
- **Model lane.** Weights are Glicko2 significances on merkle-DAG edges, composed by Unicode
  reference, trunk→leaf. Raw per-edge floats produced a multi-hundred-GB blowup.
- **Comments state constraints only.** Ownership, ordering, contract,
  why-not-the-obvious-alternative. History lives in commits.

## 4a. Ask the catalog before writing anything

`SELECT * FROM ops.api()` lists **442 installed operations**, and `ops.*` alone holds **63**.
Read that first. A session that skipped it hand-wrote replacements for at least five
functions that were already installed, then reported the hand-rolled numbers as findings:

| written by hand | already installed |
|---|---|
| index audit over `pg_stat_user_indexes` | `ops.index_usage_report`, `ops.index_usage_detail`, `ops.index_health` |
| partition skew over `pg_class` | `ops.partition_pressure`, `ops.consensus_partition_pressure` |
| entity/attestation counts | `ops.substrate_counts`, `ops.substrate_pulse` |
| geometric comparison | `ops.metric_ladder`, `ops.metric_ladder_words` |

The installed ones are better: `ops.index_usage_report()` returns the whole estate in 121 ms
— **4,075 indexes / 226 GB, of which 1,707 are never scanned (31 GB) and 1,237 more are under
100 scans (95 GB)** — where the hand-rolled query covered two tables and mis-grouped them.

The one-line index: `SELECT proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname='ops' ORDER BY 1;`

## 5. Working mode

- Technical work only.
- Reads are the cost centre: 73% of historical tool calls were read/search motion, and every
  call re-transmits the whole conversation. Batch independent calls into one block. Extract with
  one `grep -n` / `jq` / `sed -n 'A,Bp'` and read only the matched region — paging a file with
  repeated Read offset/limit calls cost ~22M cache-read tokens in one run, and a single such
  call billed 706,793 cache-read for 757 output.
- Use native query flags (`gh --jq`, `jq`, `--format`) rather than a generated parser.
- Install and operate in place — the real path, the real database, the real command.
- Writes and state changes follow from the request.
- Work the named target.
- Read-path timings measure read paths; core throughput is measured on the core.
- Check `git worktree list` and open PRs before writing a fix — many agent sessions run
  concurrently and the fix is often already in flight.
- `Justfile` and top-level CMake are the entry points. `just --list` for canonical task names.

## 6. Layout

- `engine/` — core engine (top-level `CMakeLists.txt`)
- `app/` — `Laplace.Substrate`, `Laplace.Endpoints.Mcp`, `Laplace.Endpoints.OpenAICompat`,
  `Laplace.Cli`, `Laplace.Chess`
- `web/`, `extension/` — front ends
- `db/` — schema and migrations (prose here is stale; verify against callers)
- `deploy/`, `scripts/`, `cmake/`, `external/` — ops, helpers, build support
- `test-data/` — fixtures

## 7. Running SQL

MCP `op` executes any installed function against the live substrate; `api` lists the catalog.
`psql` as the login user fails with `role "ahart" does not exist` — a missing role, not a
permission wall, and it does not block SQL. Use `op`, or
`psql -U laplace_admin -h /var/run/postgresql -d laplace`.

Installing the extension: `scripts/pipeline.sh` → `cmake --install`, then `sync-extension` →
`ALTER EXTENSION`.

The installed MCP binary is `/opt/laplace/app/mcp-runtime/Laplace.Endpoints.Mcp`; it stays as
installed and running. Match library versions before installing anything into a preload path —
the 2026-08-13 outage was a symbol mismatch.

MCP surface: `ingest`, `infer`, `query`, `walk`, `witness`, `taxonomy`, `recall`, `translate`,
`pipeline`, `op`, `api`, `bubble`, `chat`, `facts`, `leaders`, `sense_audit`, `source_status`,
`health`.

## 8. Two layers

The **composition core** and the **read paths built on it** are different systems with different
measurements.

Core, benchmarked: **≥500k tokens/sec floor on commodity CPU, no GPU required**, and a
**measured 10x with a 1080Ti** when present (GPU is a multiplier, never a requirement). No
session has produced a benchmark contradicting these.

**There was no harness for that figure until 2026-08-15** — `just --list | grep bench` gave
only `build-perfcache` / `verify-perfcache`, `ninja -t targets all | grep bench` gave nothing,
and `BenchCommands.cs` holds only `svd-exact-bench` and `model-bench`, neither of which
measures composition. The number was true and unreproducible, which is the same shape as an
unmeasured dial. `scripts/bench-compose.py` now measures it on the core — UTF-8 in,
`content_witness_tree_build` out, no database, no COPY, bounded first-party corpus so the
input cannot drift between runs.

Measured 2026-08-15, 875 documents / 67.9 MB / 67,899,577 codepoints, three runs within 0.4%:

| | per thread |
|---|---|
| codepoints/s | **1,859,000** |
| BPE-equivalent tokens/s (4 chars/token) | **464,800** |
| tier-tree nodes/s | **4,555,400** (166,383,573 nodes built) |

Read that as a **floor**: single-threaded, on a box with 12 physical cores and another agent
working, and `content_tree_build` is lock-free and per-call (`ContentIngestAdapter.cs:41`;
the `Decomposer.cs:265` this file used to cite holds no such symbol) so it fans
out. The aggregate figure is deliberately not stated here — nobody has measured it, and
multiplying by a core count is how the original number became folklore.

The unit matters when comparing to a model. A BPE token is a subword byte string whose meaning
lives only in the weights; a Laplace constituent is an entity with a merkle id, a coordinate,
and typed edges carrying ratings and witness counts. Tokens/sec across the two is a category
error in both directions — quote the codepoint figure, which is what the core actually consumes.

Read paths: the timings in §9. Slow surfaces there are query plans, partition scans, and a
resolver selecting the wrong token — defects layered on the core. Refuting a throughput figure
requires benchmarking the same path under the same conditions.

## 9a. Re-measured 2026-08-14 later, live via psql (supersedes §9 where they differ)

Measured against the running substrate after landing f53040af (derived kappa), d61e267d
(belief-first walk ranking, geometry opt-in) and cd11acdf (infer bias probe):

| surface | recorded in §9 | measured now |
|---|---|---|
| `converse.infer('the capital of France is')` | 5.4s | 54.0s before cd11acdf, **5.75s** after |
| `converse.chat('What is a wolf?')` | 41.6s | **5.46s** |
| `walk('gravity')` depth 3 / breadth 5 | `57014` timeout | **371ms**, 45 rows |
| `consensus.walk_branches` on the *wolf* word, depth 2 | — | **2.25s** |
| `converse.recall('wolf')` | 2.1s | **1.41s**, correct gloss, eff_mu 3654.227 |
| `converse.resolve('the capital of France is')` | — | **34ms** |
| `taxonomy.tree(converse.resolve('wolf'))` | *lobo* / comics character | reproduced, then fixed |

**Correction to §10.** `68d289500d11bd916b195034b485f282` is **the word `wolf`**, not a synset:
`laplace.word_id('wolf')` returns exactly that id. `converse.resolve('wolf')` returns it and is
**correct**. The taxonomy failure was never a resolution failure — `taxonomy.tree` calls
`taxonomy.top_synset`, whose parameter is literally `p_word` and whose body is
`bubble_up(p_word, NULL, 1)`, and `bubble_up` ranked the English word's own translations above
it: measured lobo 1455.04 (Portuguese), lupo 1425.95 (Esperanto), 狼 1425.26, **wolf 1407.68**,
lupus 1406.06 — decided on `base_eff_mu` alone, one scalar, which is the §7 violation.

**The language mechanism already existed and was never wired.** `converse.prompt_language_top`
returns English for "What is a wolf?" in 36ms; `converse.word_language` returns the primary
language of any word (wolf→English, lobo→Portuguese, lupo→Esperanto). No resolver took a
language argument. A boolean "has an edge to the query language" does **not** discriminate —
lobo carries English at eff_mu 1836.9 over 24 witnesses — so the constraint has to be on the
*primary* language, which `converse.word_language` already defines. Landed as a constraint (not
a score) in `bubble_up` (06c03660), `infer` (97a9a4ed) and `chat` (a4d74bde).

**Result of that, measured live.** `chat('What is a glacier?')` → "Glacier is a slowly moving
mass of ice. Glacier is a kind of ice mass, which is a kind of formation, which is a kind of
object, which is a kind of physical entity. Glacier has parts such as moraine, icefall, neve,
ice. Glacier is related to glaciate." (4.10s). `infer('What is a glacier?')` → layer, frozen
fresh water, ice mass, slowly moving river of ice.

## 9b. The remaining election defect, located exactly (2026-08-14)

**`converse.prompt_coherence` collapses each token to one sense before the seat.** For
"What is a wolf?" its entire candidate field is **er, and, hä, ingurgitate** — the animal sense
of *wolf* is never a candidate at all. *ingurgitate* is the verb sense ("to wolf down"), so the
token is represented by the wrong sense before any ranking happens. No re-ranking downstream can
fix a field the right answer never enters. This is the same mistake `infer`'s own comment names
for bias tokens ("collapsing it to one sense before the intersection is the KVP mistake this
function exists to not make") — committed by the topic seat itself.

**The naive joint-belief fix is worse, measured.** Scoring each candidate by the max adjudicated
`eff_mu` to any candidate of a *different* token, over the full `lexical.senses` distribution,
seats **LATIN SMALL LETTER E** (1746.83), then *in*, then **LATIN SMALL LETTER I**. Ubiquitous
tier-0 atoms carry high-belief edges to everything, so max-belief is a frequency prior in a new
costume — the same wall that produced the revert in 4d658df9 ("edge mass is a frequency prior").
Do not ship that shape.

**What `just eval`'s election_correctness actually measures:** `prompt_coherence_rank1` calls
`converse.prompt_coherence` through the API and applies its own `_elector_key` — the C elector,
unmediated. SQL-side seat constraints in `infer`/`chat` cannot move that number by construction.
It reads 1/6, with forward_hygiene 4/4 clean and latency p50 2.90s (was 3.77s). Its baseline was
recorded 2026-08-07 against a 4.3M-entity substrate with 11 sources; live is ~110M with 20, and
`advisory_until` expired 2026-08-10 — the fingerprint drift is expected, not a regression signal.

**Why the canonical fix did not land here:** `prompt_coherence` is C
(`prompt_coherence.c`), and `scripts/pipeline.sh` reports `preloaded .so unchanged — no PG bounce
needed (SQL-only change)` for every SQL change this session; a C change to that library requires
the preload bounce that pipeline.sh gates on (`scripts/pipeline.sh:580,624`), i.e. a postgres
restart. Every fix above was therefore made at the SQL callers, which is per-caller rather than
canonical — a §15 smell that should collapse into `prompt_coherence` when a bounce is acceptable.

## 9. Measured state of the read paths (2026-08-14, live MCP, verbatim — see §9a)

Working:

- `recall("wolf")` — correct gloss, ranked above competing senses, eff_mu 3654.2 — 2.1s
- `bubble("wolf")` — multilingual sense frontier, 8–9 witnesses per row — 0.9s
- `source_status` — 19 sources with run status and evidence counts — 1.6s
- `ops.api` 24ms, `converse.resolve` 19ms, `converse.resolve_last_word` 4.6ms. Transport
  overhead is milliseconds; slow surfaces are slow queries, not call overhead.

Failing:

- ~~`infer("the capital of France is")` → *direction, location, earthly branch* — 5.4s~~
  **MISDIAGNOSED SINCE THIS LINE WAS WRITTEN. Corrected 2026-08-15, measured.** This is not an
  election defect and no amount of ranking work will move it. `word_id('France')` carries
  **only lexical relations** — HAS_POS (NOUN/PROPN), HAS_LANGUAGE, IS_SYNONYM_OF, FORM_OF,
  HAS_DEFINITION. `CAPIT` appears in **zero of the 233 canonical relation names** in
  `engine/manifest/relation_types.toml` (count with `canonical =`, not `^\[\[relation\]\]`,
  which gives 210 and is wrong). *nominative*, *singular* and *franc* are the CORRECT lexical
  neighbours of the token — grammatical case, number, and a lexically adjacent currency. The
  ranker is returning the best answer available from the edges that exist.
  - **The fact is nonetheless in the substrate, and finding it requires not making the mistake
    §4 already warns about.** `France → Paris` under any relation returns **0 rows**, and that
    is the sequence-is-geometry law working, not absence. The capital fact lives as an
    adjudicated composition: `Paris --HAS_DEFINITION--> "Paris (the capital and largest city of
    France)"`, rating 1.83e12 over **10 witnesses**, whose trajectory carries
    `capital → France` at **gap 5** with separators dropped. Its subject anchors to ILI
    (`i102495`, `i83645`, `i84698`) with **64 IS_TRANSLATION_OF surfaces** — so a relation
    extracted once from an English gloss lands on the language-independent hub and is true in
    all 64. "The seed source is English" is not a limitation; the ILI hub is the distribution
    mechanism and it is already populated.
  - **The real defect class is unextracted typed relations.** Thousands of *is the capital of*,
    *is located in*, *was born in* facts sit inside HAS_DEFINITION objects, invisible to
    traversal and to the foundry adjacency read, because no analyzer promotes gloss prose to
    typed edges. Spec 08 sanctions exactly that lane: parsed relations are **calculated
    testimony** carrying analyzer identity, version, inputs and recipe, competing as another
    witness and never overwriting the recorded gloss.
  - **Do not test a pair when diagnosing this class.** `WHERE subject_id = X AND object_id = Y`
    returning 0 proves nothing here; list what the entity actually carries, resolve the
    relation type ids through the manifest (they are BLAKE3 of the canonical name, so
    `resolve_name` renders them as hex), and read the trajectory of the compositions that
    witness the adjacency.
- `taxonomy("wolf")` → *lobo* (comics character) under *fictitious character* — 0.46s
- `chat("What is a wolf?")` → "Hä is huh? uh?. Hä is related to formazza and uri and
  gressoney." — 41.6s
- `translate("water")` → `substrate unavailable: Exception while reading from stream`
- `query(reason, wolf, dog)` → same stream exception
- `walk("gravity")` → `57014` statement timeout
- `health` → 9.0s
- `facts("water")` → one entity id `979cf90cd158a187ba0a101784d1decd` carries water, *dar* (to
  give), and urine. Entity collision at ingest, not a ranking fault.
- ~~`source_status` reports nine sources with `evidence_approx: 0` … a gate failing open.~~
  **FALSE, corrected 2026-08-15.** No gate failed and nothing was missing. `source_status` drew
  `evidence_approx` from `ops.source_counts_approx()`, which reads the **parent** table's
  `pg_stats` most-common-values — a list holding exactly **10** entries, so only the ten largest
  sources could ever report non-zero. The nine hold **1,642,792 attestations** between them:
  ChessPgn 1,472,737 · ISO639 42,931 · WordFrameNet 40,170 · ChessOpenings 28,546 · VerbNet
  25,288 · SemLink 16,404 · MapNet 14,175 · ChessBook 2,540 · UserPrompt 1. Raising the
  statistics target cannot fix it: with one source at 84% of 371M rows, a source at 0.004% is
  about one row in the sample. The estimator now covers only the head, and the tail is counted
  exactly (the sources it cannot see are by definition the small ones). Full listing 1.6s → 4.8s.

## 10. Open defect: phrase resolution, and an absent language prior

Reproduction, on "What is a wolf?":

- `converse.resolve` → `1704aaef…`; `realize.resolve_name` renders null
- `converse.resolve_topic` → a different unnamed node, 2.7s
- `converse.resolve_last_word` → **QUESTION MARK** (the punctuation codepoint)
- none return the wolf synset `68d289500d11bd916b195034b485f282` that `bubble("wolf")` resolves
  correctly
- `converse.resolve_audit` reports every token `resolved: true`, including `a` and `?` at tier 0
  ("floor only — codepoint, no word sense"), and `top_language` **null** for all five

`scripts/sql/converse-audit.sql:136` documents the same class ("leading article wins leftmost
tie-break in resolve_phrase"). Single-word surfaces (`recall`, `bubble`) work because they never
enter this path.

Rejected fix — do not implement: selecting the highest-rated token. Measured top scores were
*what* 1337.1, *is* **1441.3**, *wolf* 1455.0. Wolf wins by 14 points over a copula; that margin
is a tuned constant in disguise and inverts on other sentences.

Deeper defect: `bubble("wolf")` ranks **lobo** above **wolf**, and `bubble("what")` returns
**wat** and **kas** (Dutch, Estonian). English surfaces lose to their own cross-lingual synonyms
because the ILI hub collapses a term into a language-blind synonym set and the ranking carries
no language prior. Same defect surfaces in `taxonomy`, `infer`, `chat`, `bubble`. Resolution
needs the query language as a constraint; `resolve_audit.top_language` exists and is unpopulated.

## 11. Walk scoring — measured 2026-08-14, live `laplace` via psql -U laplace_admin

The recorded cause of the slow unfiltered walk was wrong, and so is #351's. Measured on one
200-node frontier (21,413 candidate edges), same batch edge query text:

| batch edge query | planning | execution |
|---|---|---|
| as shipped (2× `laplace.v_word_points` LEFT JOIN) | 104 ms | **30,305 ms** |
| geometry joins removed, everything else identical | 20 ms | **408 ms** |

- `laplace.consensus` is `LIST (type_id)`, **27** partitions — not "~200+ leaves" (#351). Its
  Append is in *both* plans, so it is not the cost.
- `laplace.physicalities` is **`HASH (id)`, 64 partitions** — NOT "RANGE-partitioned by
  hilbert_index". `generate_walk.c`, `walk_branches.sql.in`, and #350 all state the hilbert
  premise. **#350's proposed fix cannot work**: there is no hilbert band to scope a scan to.
- Both geometry features join `physicalities` by `entity_id` while it is partitioned on `id`, so
  the key cannot prune and every probe fans out across all 64 — twice per edge.

**The scoring defect this exposed.** `base = relation_rank × walk_edge_weight` measured **max
1.95e-20** over those 21,413 edges. `walk_edge_weight` is `signed_mu × exp(−kappa·rd) ×
saturation` with `consensus.foundry_rd_kappa() = 1.0` applied to `rd` in raw rating units (live
rd 21–288): `exp(−265) ≈ 7e-116` at the average. The geometry bonus is `+2.0` and topic bias
`+3.0`, **additive**. So `base + bonus` discarded the adjudicated verdict entirely and the beam
ranked on angular proximity — the inverse of §5 ("all ranked reads order by belief") and §9
(geometry is instrument-tier; point proximity is not the relatedness signal), and the opposite of
what the scorer's own comment claims.

`kappa = 1.0` is an unmeasured dial with foundry blast radius (`relation_plane`,
`consensus_layer_plane`, `consensus_type_plane` all consume it). **Unmeasured, and no session has
measured it.** Measuring `kappa` across those three consumers is undone agent work; land the
measurement before proposing a value.

## 12. Provenance

Nothing in this file that is not a measurement is authoritative. Prior agent-written notes have
been wrong about what this system is; verify against source, callers, and live behavior.

Knowledge is ingested into a content-addressed Postgres substrate. Weights are Glicko2
significances on merkle-DAG edges. A Foundry path exports GGUF from the substrate.
