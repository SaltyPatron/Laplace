# The fold was neutralized at ingest — measured 2026-08-23

Every conversational and generative failure in this substrate traces to one mechanism.
This document records the measurements, the mechanism, what was fixed, and what is not.

## The mechanism

Glicko-2 rates by outcome against an opponent. The ingest path supplied:

- **score ≡ 1.0** — the `Categorical` overload hardcodes `score = confirm ? 1.0 : 0.0`
  (`engine/core/src/attestation_engine.c`) and never reaches `score.c`. 80.26% of
  139,559,212 attestations carry exactly 1.000; 14.35% exactly 0.750; **3.16% vary.**
- **outcome ≡ Confirm** — no lexical decomposer has ever passed `confirm: false`.
- **opponent μ ≡ 1500** — a fixed neutral prior (`consensus_fold_math.h`).

Every observation is therefore a win, of fixed magnitude, against a fixed-strength
opponent. Under those inputs rating rises monotonically with observation count and RD
shrinks with it, so `eff_mu = rating − 2·rd` is a monotone function of **witness count**.

**Consensus became frequency.** The fold is a hit counter.

That single fact surfaces everywhere and is why fixing the read path never worked:

| symptom | measurement |
|---|---|
| election picks function words | `a` specificity 0.017056 beats `glacier` 0.001045 on `What is a glacier?` |
| senses cannot be separated | eff_mu spread **0.0** across all senses of `what` and `the` |
| 82% of WordNet senses tie | magnitude 0 → `laplace_score_fp` returns exactly 0.5 |
| generation emits frequency | `dog → them they them they`; hubs dominate the lm_head |
| ratings carry no signal | `HAS_RATING` — the relation whose object IS a rating — has **1 distinct value** |
| single-witness cells | 116,169,744 of 127,023,784 (**91.5%**) |

## Refutation is absent, so belief can only rise

`outcome ∈ {Refute=0, Draw=1, Confirm=2}` is what makes this a rating system rather than
a tally — losses are what make a rating fall. The sources ship losses:

- VerbNet `<PRED bool="!">` — **1,002 of 6,744 predicates (14.9%)**, entered with the sign
  stripped, so the substrate asserts the negation of what VerbNet states
- OMW `*-changes.tab` — **3,279 REMOVED** rows, never globbed
- CILI `changes-in-wn31.csv` — **76 deprecated** ILIs, never globbed
- ConceptNet `Not*` — **29,547** rows modelled as separate *positive* relation types, so the
  refutation never meets the cell it refutes

## Why consensus ≈ attestations

139,556,085 attestations produce 127,021,587 consensus cells — 1.1 witnesses per cell, and
**91.46% of cells have exactly one**:

| witnesses | cells | share |
|---|---|---|
| 1 | 116,169,744 | 91.46% |
| 2 | 6,245,260 | 4.92% |
| 3 | 1,791,717 | 1.41% |
| ≥4 | 2,817,063 | 2.22% |

A cell is keyed `blake3(subject‖type‖object)`, so two witnesses meet only when they mint the
identical triple. Where identity is shareable the fold works — `GAME_HAS_MOTIF` is only
34.8% single-witness. Where the subject is unique per occurrence, a second witness is
**impossible by construction**: `ANALYZED_AT`, `HAS_DTZ`, `HAS_WDL` and `HAS_EVENT` are all
**100.0%** single-witness and `HAS_PARSE` is 99.2% — 36.8M cells that can never be rated.

## Why entities outnumber geometries

71,321,006 entities against 47,577,055 placements. The gap is bookkeeping, not missing
content — real content is placed (tier 0 and 1 at 100%, tier 3 at 99.9%, and `UD_Parse`,
`Chess_Game` and `Document` at 100%):

| tier-4 type | entities | with geometry |
|---|---|---|
| **Chess_AnalysisMarker** | **15,009,212** | 0% |
| UD_Parse_Occurrence | 2,177,867 | 0% |
| Chess_Playing | 982,515 | 0% |
| Chess_Event | 219,141 | 0% |
| FrameNet_Annotation_Occurrence | 28,941 | 0% |

Tier 2 accounts for a further 6.3M (81.3% placed).

## Rating spread across the largest relations

Sampled 50,000 cells per relation:

| relation | cells | distinct ratings |
|---|---|---|
| HAS_DTZ | 11,868,348 | **1** |
| HAS_WDL | 11,868,348 | **1** |
| HAS_EVENT | 1,913,044 | **1** |
| ANALYZED_AT | 12,863,059 | 2 |
| GAME_HAS_ECO | 1,863,291 | 2 |
| HAS_LANGUAGE | 15,438,786 | 296 |
| HAS_POS | 8,099,393 | 1,053 |

~38M cells — roughly 30% of the substrate — carry no ranking signal. Some of that is
legitimate (an exact tablebase result ships no confidence to rate); the point is that
**nothing measured it.**

## The verification layer certified the axis that was healthy

Three independent mechanisms, all green throughout:

1. **`decomposer-gates.json` entries are row-count minima.** A relation whose ratings are
   bit-identical passes cleanly. Volume was never the broken axis.
2. **`ElectorArchitectureGateTests`** proved six copies of the topic election were
   byte-identical. It could never prove any of them correct, and its own header records
   the drift it failed to prevent.
3. **`PhysicalityType_ProductionEmitters_UseContentOrProjectionOnly`** forbade production
   decomposers from using `PhysicalityType.Set` — the type that exists, per its own
   comment, so shapes "whose vertex order carries no sequence meaning" stay out of the
   `WHERE type = 1` indexes. The law was written and made unreachable in the same
   repository, so the only reachable type was the one the law says not to use.

(3) is the worst of the three: a missing test says "we did not check." That test said
"we checked, and we will fail code that does the semantically correct thing."

## Fixed

- **Relation rank reaches the resolved builder** (`669f4c99`).
  `laplace_attestation_resolved_build` took `witness_weight` raw while both siblings
  compute `rank * trust_weight`, so every `NativeAttestation.CategoricalResolved` site
  folded at source trust alone. `HAS_SYNSET_KEY` entered at 0.85 instead of
  0.36 × 0.85 = 0.306 (φ≈78 against ≈252). Pinned by
  `ResolvedBuildAppliesRelationRankLikeSurfaceBuild`, verified to fail against the
  previous body.
- **Parse structures left the content lane** (`669f4c99`). 2,132,050 of 46,542,360 type=1
  physicalities were UD parse structures, so `generation.trajectory_continuations`
  returned annotation entities as continuations of words — `hot → ud/misc-key/…` at 544
  above `hot → water` at 502. `ParseStructure = 8` added; the gate that forbade the
  correct type is now `PhysicalityType_ProductionEmitters_DoNotUseReservedTypes`.
- **UD MISC values are content** (`316f0406`). 3,313,800 hex-slug canonical names —
  97.9% of `canonical_names`, 892 MB, 0 consensus edges — replaced by content addresses,
  so `Gloss=dog` is the entity `dog`.
- **Rating-spread gate** (this commit). `decomposer-gate-check.py` now fails any gated
  relation with ≥1000 cells and one distinct rating. It is a **default rule, not an
  opt-in field** — an opt-in spread check reproduces the blindness it removes, because
  the relations nobody annotates are the ones that degrade. Exemption requires a stated
  reason in the gate (`"constant_rating_ok": "…"`), which reviewers can challenge.

- **VerbNet negation is a Refute** (this commit). `<PRED bool="!">` was never read, so
  **1,002 PREDs** in verbnet-master were deposited as positive
  `ENTAILS` — the substrate asserting the negation of what the source states. A further
  **39** carry `bool="?"` (optional), which is a Draw: `laplace_score_fp(0, m)` is exactly
  0.5. The negation is deliberately NOT in the predicate id preimage, so an assertion in
  one frame and a denial in another meet in one consensus cell and contest it. Pinned by
  `NegatedPredicate_FoldsAsRefute_AndOptionalAsDraw`.
- **OMW retractions are ingested as Refutes** (this commit). `<lang>-changes.tab` was
  never globbed: **3,279 REMOVED rows across 26 files**, each saying a lemma is no longer
  a member of a synset, so membership could only ever accumulate. The retraction now
  refutes the SAME triple the `wn-data` row asserts, in the same language context, so it
  meets the assertion in one consensus cell and contests it. The 129 MODIFIED rows are
  deliberately untouched — the action says the entry changed, not that membership was
  withdrawn, and guessing which half changed would invent testimony the source did not
  give.
- **ConceptNet and Atomic2020 denials refute what they deny** (this commit). `NotDesires`,
  `NotUsedFor`, `NotCapableOf` and `NotHasProperty` mapped to separate *positive* relation
  types (`NOT_DESIRES`, …), so **29,547 rows** of negative evidence folded into cells that
  could never contest the assertion they contradict — "a fish cannot walk" landed nowhere
  near "a fish can swim". They now map onto the relation they deny, with the source's own
  weight **negated**: `laplace_score_fp(v, m)` scores `v < 0` below 0.5, so the row folds
  as a Refute whose strength is ConceptNet's own confidence. That sign channel always
  existed and the full 34M-row file never used it once (0 negative weights). Atomic2020
  carried the identical mapping and is fixed the same way.
- **PropBank `<lexlinks>` are read, with their confidence** (this commit). The element was
  never opened. It carries the only explicit graded confidence in either frame corpus —
  **16,250 rows at 0.8 or 1.0** — mapping a roleset to a FrameNet frame or VerbNet class.
  The mapping was either absent or arrived via `<rolelink>` at a flat unscored 1.0,
  discarding a hand-curated distinction the source recorded deliberately. Emitted before
  the role pass and sharing its dedup set, so the confidence-bearing witness is the one
  that lands.
- **ConceptNet's source count reaches `observationCount`** (this commit). The corpus states
  how much support each assertion has in its `sources` array and it went nowhere: every row
  folded at 1, so an edge **465 sources** agree on folded exactly as hard as one asserted
  once. **96,831 rows (5.4%)** list two or more. `RelationTripleRecord` now carries
  `ObservationCount` (default 1, so a source that says nothing is unchanged).
- **OMW's frequency file is ingested** (this commit). `wn-freq-*.tab` — **4,981 rows** of
  "this lemma was observed N times for this synset", the only per-row magnitude the corpus
  ships — was never matched by the glob list. It witnesses the same membership the
  `wn-data` row asserts and now folds into that cell as a second, *scored* witness, so a
  lemma observed 50 times outranks one observed once instead of both entering at the
  categorical constant. A row with no usable count is dropped rather than defaulting to 1.
- **A recipe's declared operators are no longer silently zeroed** (this commit).
  `RecipeDescriptor.Parse` inferred `compile` from `lm_head` — and `lm_head` itself
  defaults to `trajectory` — so a recipe that explicitly declared `relation:IS_A`,
  `relation:HAS_PROPERTY` … and merely omitted `compile` selected continuation mode.
  `FoundryCommands` then applies `OpAttnScale = OpResidScale = 0` to every operator
  outside the whitelist (`context`, `trajectory`, `sentence_order`, `relation:PRECEDES`).
  The planes were still read and their **edge counts still printed**, so the census said
  the capability was present while the emitted tensors had it removed. Continuation-only
  must now be requested explicitly, and when it *is* requested the dropped operators are
  named on stderr.
- **Game metadata hangs off the trunk instead of folding** (this commit). The chess
  analysis watermark was emitted as the manifest relation `ANALYZED_AT`, so it folded:
  **12,891,661 attestations → 12,863,059 consensus cells, 100% single-witness, 2 distinct
  ratings.** It can never be anything else — the subject is one game and the object is the
  analyzer version, so the triple is unique by construction and no second witness can
  exist to rate it against. The substrate already had the right shape and uses it
  elsewhere: `FileEntity.MetadataRelationTypeId` and `LayerCompletion`'s
  `HasLayerCompleted` are substrate meta-types, minted inline, never in
  `relation_types.toml`, never folded — verified live at **209 / 0** and **8,995 / 0**
  attestations/consensus. A game's analysis version is provenance, fetched when asked,
  exactly like a file's name and mtime.
- **Synthesis is gated on the artifact, not its size** (this commit). CI's entire check was
  "the file exists and is over 50 MB" — a 50 MB file of zeros passed. Two gates now run.
  `verify-gguf-nondegenerate.py` needs only the artifact and fails any tensor that is
  entirely zero or whose rows are identical, which is exactly what a declared-then-zeroed
  operator, a constant FFN gate, or hub collapse looks like. And
  `verify-model-behavioral.py` — which existed, names "SIMULATED success" as the project's
  defining failure mode in its own header, and **was invoked by no workflow** — is now
  wired in. It could not run: it assumed a `llama-completion` binary, and its SQL called
  the bare `render(...)` that has not resolved since the purpose-schema migration
  (#862/#957). It now has an ollama backend (the runtime that is actually installed) and
  schema-qualified SQL, verified end to end against a stored substrate GGUF where it
  correctly returned `BEHAVIORAL FAIL: 0/4 probes`.
- **FrameNet's annotated-instance count reaches the fold** (this commit). Every
  `<pattern>` states `total="N"` — **192,241 of them in framenet_v17** — and it was never
  read. `HAS_VALENCE_PATTERN` folded at whatever number of times the pattern *string*
  happened to repeat in the XML, a structural artifact of the file layout, so a pattern
  annotated 87 times and one annotated once could enter at the same strength. The stated
  total is now the `observationCount`. A `valenceUnit` outside a `<pattern>` carries no
  total of its own and enters at 1 rather than borrowing a number the corpus never gave it.
- **CILI's change log is ingested — and is deliberately NOT a Refute** (this commit).
  `SelectMapInputs` globbed `ili-map-*` only, so `changes-in-wn31.csv` was never read: 76
  deprecated ILIs whose withdrawal the corpus states outright. It is recorded as a
  version-scoped meta-fact rather than a refutation, because **`laplace.consensus_id` is
  `blake3(subject, type, object)` — context is not in the cell key.** Refuting
  `ili IS_TYPED_AS concept` would deny it flatly and contradict the wn30 testimony sharing
  that same cell, so version-scoped denial is not expressible as an outcome in this schema
  and forcing one would corrupt an unscoped claim. Recording it still converts "absent from
  `ili-map-wn31`" — which spec 05 calls *unknown*, not refutation — into a stated fact a
  reader can fetch. The 274 `new` rows are not re-asserted: the `ili-map` file already
  carries those mappings, and re-emitting them would double-witness one source.
- **SPI plans are parallel-eligible** (this commit). `SPI_prepare` plans with parallelism
  DISABLED, and **no file in the extension used `CURSOR_OPT_PARALLEL_OK`**. The successor
  probe is a GIN containment scan over all 64 hash partitions of `physicalities` — the
  partition key is `id`, the predicate is on constituents, so nothing prunes. Standalone
  the planner uses a Parallel Append with 7 workers at ~42ms; through `SPI_prepare` the
  identical query ran serially. Measured warm after the change:
  `trajectory_continuations` **687ms → 173ms**, `geometry_successors_batch`
  **975ms → 127ms**. Results bit-identical (`New→York` 6407, `氷→河` 72).

  Applied to **all 29 read-only plans across 15 files**, not just the two that were
  profiled. `fold_route.c` is exempt because its plans execute read-write, and
  `SpiParallelPlanGateTests` both forbids a new serial read-only plan and checks that the
  exemption really does execute read-write — verified to fail when one call is reverted.

  Cursors take `cursorOptions` too and every call passed `0`, so that was set as well —
  but **the cursor change is worth ~6%, not the 3.4× I first claimed.** Measured warm over
  repeated samples: with the flag 1438–1488ms, without it 1571–1576ms. The 6108ms I
  compared against was a single cold sample. That is consistent with the documented
  limitation that a cursor fetched via `SPI_cursor_fetch` executes in a single backend even
  with `CURSOR_OPT_PARALLEL_OK`.

  Converting the draining cursor to a one-shot `SPI_execute_with_args` — which *is*
  parallel-eligible, since `SPI_execute` sets `CURSOR_OPT_PARALLEL_OK` itself — measured
  **1577ms**, no better, so it was reverted rather than kept for tidiness. The fetch
  mechanism is not the cost: `taxonomy.bubble_up_batch` alone is **696ms** of the ~1450ms,
  and the per-row `converse.word_language(b.sense_id)` adds ~130ms. Elector output
  identical throughout (`a` 0.017056, `what` 0.013987, `glacier` 0.001045).
- **`resolved_scored_build` also applies rank** (this commit) — it had the same raw
  `witness_weight` defect as `resolved_build`.

## Not fixed

- **Magnitude at the call sites.** 199 `NativeAttestation.Categorical` call sites, **5**
  pass a magnitude, 191 default `confirm: true`. The sources ship the magnitudes:
  WordNet `tag_cnt` + `sense_number`, ConceptNet `weight` (31.8% ≠ 1.0) and `sources[]`
  (96,831 rows with ≥2 contributors), PropBank `lexlink confidence` (16,250 rows at
  0.8/1.0), FrameNet `total=` (290,405 values), OMW `wn-freq-ind.tab`.
- **Version-scoped refutation is not expressible.** `consensus_id` excludes context, so a
  denial that holds in one release and not another cannot be an outcome without corrupting
  the unscoped cell. CILI's deprecations are recorded as meta-facts instead. If
  version-scoped adjudication is wanted, the cell key has to carry it.
- **Proven on a fresh isolate.** `just decomposer-test verbnet` seeded
  `laplace_d_verbnet` with the fixed decomposer and the gates pass, including the new
  rating-spread gate. The outcome distribution is the point: `ENTAILS` on the **old** live
  substrate is 10,073 attestations, **all Confirm, zero refutations**; in the fresh isolate
  it is **8,131 Confirm + 1,419 Refute**. The fold now receives negative evidence it
  structurally could not before. This is why the live database is not worth tuning against —
  it is the output of the broken decomposers, and `CLAUDE.md` makes db-reset + reseed the
  upgrade path.

- **The fold is the ingest bottleneck, and it is provisioned as if it were not.**
  Measured on the live foundation seed via `pg_stat_statements`, scoped to the `laplace`
  database and top-level statements only:

  | statement | total | calls | mean |
  |---|---|---|---|
  | `consensus.upsert_type` (fold) | **3,189s** | 1,058 | 3,014ms |
  | COPY attestations | 639s | 865 | 739ms |
  | `highway_mask_deposit` | 464s | 967 | 479ms |
  | COPY physicalities | 354s | 785 | 451ms |
  | COPY entities | 128s | 856 | 149ms |

  The fold costs **2.8× the entire apply side** (3,189s against 1,121s combined), while
  `IngestSizing` gives it `applyPartitions − ConsensusFoldPoolHeadroom` connections against
  the COPY fan's `applyPartitions` — fewer. Its comment calls the fold "the elastic
  background owner, so it cedes the headroom"; the measurement says the fold is the
  dominant cost, not background. A consumer that is both more expensive per unit and less
  parallel than its producer accumulates backlog monotonically, which is what
  `DrainFoldsAsync` then waits out: CILI spent **269s of 336s** there.

  `DrainFoldsAsync` only awaits already-dispatched work — there is no terminal fold pass,
  and `FoldRunAfterAsync`/`AllocateFoldRunWidths` do give a single-relation source the full
  connection width. The imbalance is service rate, not lost parallelism.

  **Caveat on method:** `pg_stat_statements` is cluster-wide and `track = all`. An
  unscoped reading sums every throwaway isolate with the seed and double-counts nested
  statements inside their callers; the figures above filter on `dbid` and `toplevel`.
  Rebalancing the two budgets is the obvious next move and is deliberately NOT made here —
  it needs an A/B on a cluster this session does not own.

- **Hub-word successor lookups.** A common word is still pathological: `the` appears in
  2,376,395 trajectories and its continuation lookup takes ~10.7s even parallel, because
  every containing trajectory is decoded. Bounding that needs a precomputed successor
  structure, not a faster scan.
- **Test-suite load sensitivity.** Three different tests failed once each
  (`Token_command_outranks_a_stale_environment_variable`,
  `ManyIndividuallySmallFiles_StillRefineWhenCorpusExceedsSharedBudget`,
  `IngestPool_CoversItsFansAndItsObservabilityOwners`) while builds, `ctest` and `psql`
  ran concurrently. All three derive from ambient machine resources. **10 consecutive
  clean runs** on a quiet machine, with and without `LAPLACE_NO_PIN=1`, so CPU-affinity
  pinning is ruled out as the cause and the mechanism is not isolated.
- **The 892 MB of existing `ud/misc-value` rows.** The fix is ingest-side; they clear on
  the next `ud` seed.

## Why the fold counts instead of rating — the mechanism, derived

The flattening is not a tuning problem and cannot be fixed by adjusting source trust.
It follows from three facts in the code, two of which are constants.

**The opponent's rating is pinned.** `consensus_fold_apply_partial`
(`extension/laplace_substrate/src/consensus_fold_math.h`) calls
`glicko2_fold_uniform_period(st, CONSENSUS_FOLD_NEUTRAL_MU, phi, ...)`. The opponent
rating is the literal `CONSENSUS_FOLD_NEUTRAL_MU = 1500`; it is never passed in by any
caller and never varies. `scoped_consensus.sql.in` confirms this from the SQL side — the
aggregate receives `opponent_rd_fp1e9`, `observation_count` and `sum_score_fp1e9`, and no
opponent rating at all.

Internally `mu_j = (1500 - 1500) / 173.7178 = 0` for every witness in the substrate. So
`E = 1 / (1 + exp(-g(phi_j) * mu))` depends only on the CELL'S OWN rating. A cell never
plays a strong or a weak opponent — only a more or less CERTAIN one.

**Every observation is a win.** 5,297 REFUTE attestations against 15.4M CONFIRMs on the
2026-08-23 seed, 0.034%; thirteen of fifteen seeded sources emit zero refutes and zero
draws. With `s = 1`, the first witness sees `s - E = 0.5` exactly, and thereafter
`s - E > 0` always. `mu` rises monotonically and `phi` shrinks monotonically, so
`eff_mu = mu - 2*phi` increases in BOTH terms, without bound, purely in witness count.

**Trust is bounded; count is not.** Trust reaches the math only through `g(phi_j)`.
Across the 28 distinct opponent RDs observed (86.16 to 348.05):

    g(phi) = 1 / sqrt(1 + 3*phi^2/pi^2),  phi = RD / 173.7178
    RD =  86.16 -> phi = 0.496 -> g = 0.965
    RD = 348.05 -> phi = 2.004 -> g = 0.671

A factor of 1.44 on the step size, and it can never change the step's SIGN. Measured
`eff_mu` bears this out exactly: single-witness cells span 801-1181 (the full 380-point
trust range), and at ten witnesses the least-trusted cell reaches 1309 — above the
most-trusted single-witness cell. **The entire 11-rung trust ladder is worth fewer than
ten witnesses.**

### What this rules out

Retuning `SourceTrust` cannot fix it: a bounded multiplier cannot beat an unbounded
accumulator. LEARNING trust cannot fix it either — over 1,014,723 contested cells
(`witness_count > 2`) every source agrees with the standing consensus 100.00%, because
"agrees with the majority" is 100% by construction when nothing dissents.

Glicko carries its information in LOSSES. With none, it degenerates to a weighted counter,
and `eff_mu` measures who asserted a triple and how often — never whether it holds.

### What this does not fix either

Deriving refutations from single-valued relations was tested and rejected. The perfectly
functional relations (`HAS_LINE_BREAK`, `HAS_EAST_ASIAN_WIDTH`, `HAS_BLOCK`, `HAS_AGE`,
`HAS_SCRIPT` — all 100% single-valued, small domains) would imply on the order of 10^8
refutations, which is why GH #535 says derivable-evidence VIRTUALIZATION. But those are
the Unicode property relations, which already carry exactly one confirmed object per
subject: there is nothing to discriminate, so the derived refutations add no ranking
power where ranking actually happens.

The negative evidence recovered in this branch — ~122,000 refutes and the substrate's
first draws, a 23x increase — is necessary and remains under 1% of the confirms. The
degeneracy is structural, and closing it means either corpora that state negatives, or a
ranking signal that is not `eff_mu`.
