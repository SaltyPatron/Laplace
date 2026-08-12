# Substrate architecture review — 2026-08-11

Session checkpoint. Storage-engine decision, geometry findings verified against source,
one live incident, and the open work queue.

Every finding below cites the file it was read from. Items marked **UNVERIFIED** are
inferences I did not confirm in code — treat them as leads, not facts.

---

## 1. Decision: no graph DB, no vector DB

Question asked: would Neo4j (provenance) or Milvus (vectors) be better/faster/more
robust than the current Postgres substrate?

**Answer: no, for both. Neither goes on the list.**

### Neo4j — rejected

| Argument | Evidence |
|---|---|
| Graph traversal is not the hot path | Exactly 3 `WITH RECURSIVE` in the whole extension: `constituents_closure`, `relate_path`, `laplace_ancestry` |
| The hot path is a numeric fold in C over an in-RAM accumulator | `src/consensus_fold_math.h`; `attestations.sql.in` — *"The fold NEVER reads this table"* |
| Partition-per-relation already **is** the relationship-type index, with knobs Neo4j doesn't expose | `consensus.sql.in` / `attestations.sql.in` — `PARTITION BY LIST (type_id)`, HASH sub-partitioning, per-partition autovacuum |
| Integrity would get **weaker**, not stronger | `consensus.sql.in` — *"Referential integrity is structural (content-addressed ids)"*, `id = blake3(subject‖type‖object)`. Unforgeable beats checked; Neo4j has no FKs at all |
| Second write path with no distributed transaction | A provenance store that can disagree with itself is a correctness regression, not a perf one |

### Milvus — rejected

| Argument | Evidence |
|---|---|
| The coords are **not semantic**, by law | spec 09 — *"The S3 4D coord is PHYSICAL/identity … It is NOT a semantic embedding; distance on S3 != meaning."* ANN would index the wrong quantity |
| **d = 4.** There is no curse of dimensionality to beat | ANN (HNSW/IVF-PQ/DiskANN) exists for d ≈ 384–4096. At d=4 exact indexing wins outright |
| Exact spatial indexing already exists | `hilbert4d.h` + `laplace_angular_distance_4d`; Hilbert-band locality |
| Attention weights can be exactly zero → sparse enumeration is available | `converse.attention` ranks on `eff_mu(rating, rd)`; absent edge = structurally absent, not epsilon. ANN top-k would *lose* exactness |
| Filters live in Postgres | Cross-system filtered-ANN is the worst architecture in the space: pre-filter defeats the index, post-filter collapses recall |

### The columnstore instinct — partially valid

Milvus is not a columnstore (ANN index + scalar sidecar; no analytic aggregates).
Neo4j is further still. But the underlying instinct points somewhere real:

| Table | Access pattern | Verdict |
|---|---|---|
| `consensus` | upsert-hot, point lookups by the fold | **stays heap** |
| `attestations` | append/merge, *never read by the fold*, scanned for audit/refold/evict | **columnar candidate** |

- Tool would be **Citus columnar** (in-Postgres table access method — no second system, no
  second write path).
- Hard constraint: columnar tables support INSERT/SELECT only, **no UPDATE/DELETE**. That
  rules out `consensus`, and complicates the attestation merge path, which updates
  `sum_score_fp1e9` additively. Viable only for cold relation partitions that have stopped
  merging.
- Accidental win: `fp1e9` fixed-point `bigint` columns compress far better under
  delta/frame-of-reference encoding than floats would.
- **Cheapest first step:** export to Parquet, query with DuckDB. Zero risk, and it answers
  "does column storage buy anything measurable" in an afternoon.

**Ranked list if effort goes to storage:** (1) measure something first, (2) BRIN on
`last_observed_at` for append-ordered partitions, (3) Parquet/DuckDB for offline
analytics, (4) Citus columnar for cold attestation partitions.

---

## 2. Geometry findings

### 2.1 Super-Fibonacci is N-dependent — and tier 0 escapes it correctly

`engine/core/src/super_fibonacci.c`:

```c
const double s        = (double)i + 0.5;
const double s_over_n = s * (1.0 / (double)n);
const double r        = sqrt(s_over_n);        // depends on N
const double R        = sqrt(1.0 - s_over_n);  // depends on N
const double alpha    = s * (TWO_PI / PHI);    // phi = sqrt(2)   — N-free
const double beta     = s * (TWO_PI / PSI);    // psi = 1.5337511 — N-free
```

Both **phase angles are N-independent**; only the **radial split `(r,R)`** — which Hopf
torus the point sits on — depends on N. Changing N slides every point in latitude while
both longitudes stay pinned.

Normally fatal to a "never move" law: adding the (N+1)th entity would re-place all N prior
coords and invalidate every `hilbert_index`.

**Tier 0 escapes this because N is a constant of the codespace, not a count of ingested
things.** Unicode is permanently closed at 1,114,112; `ByteAtoms` passes a fixed `Count`.
`laplace_nearest_entity.sql.in` — *"tier 0 coords are placed by super_fibonacci directly
ON the unit sphere."*

This is sound and deliberate. Fixed N is what makes "never move" enforceable.

### 2.2 The low-discrepancy guarantee does not survive composition

Composed entities are **not** on the spiral — they are `math4d_centroid` of their children.
Even distribution is a property of the Super-Fibonacci sequence, so it holds at the Unicode
floor and nowhere above it. Composed coords clump wherever their constituents happen to be.

### 2.3 Centroid is Euclidean; the spec calls for Fréchet

`engine/core/src/math4d.c:61` — plain arithmetic mean of the four components, un-normalized:

```c
void math4d_centroid(const double* points, size_t n_points, double out[4]) {
    ...sum all four components...
    const double inv = 1.0 / (double)n_points;   // arithmetic mean
```

spec 09 — *"metric: angular/geodesic or Frechet mean on S3, NOT Euclidean centroid
(curvature error…)"*. Euclidean-then-normalize is the **extrinsic** mean, which agrees with
the Fréchet mean for tight clusters and diverges as dispersion grows — precisely the
curvature error the spec names.

**UNVERIFIED:** commit `2537fcd7 fix(geometry): Frechet on realized curves; dual
Packed|Placement fold viewer` may already address part of this on the curve side. Not
checked.

### 2.4 Origin collapse is silent

`engine/core/src/math4d.c:81`:

```c
static double normalize4d(double v[4]) {
    const double n = math4d_norm(v);
    if (n == 0.0) return 0.0;    // silently no-ops, leaves v at the origin
```

Failure mode is **not** escaping the glome — nothing can, because placement normalizes onto
it. The failure is **collapsing inward**:

- Children antipodal or near-uniformly spread → sum ≈ 0 → the origin, the one point in ℝ⁴
  with no projection to S³. `normalize4d` returns `0.0` and leaves the vector there.
- Near-zero → the normalized direction is numerically unstable; float noise gets amplified
  into an essentially arbitrary unit vector.

Consequence: **the geometry cannot abstain.** Every entity receives a coordinate that looks
exactly as valid as every other. A correctly-placed entity and an arbitrarily-placed one are
indistinguishable by inspecting `coord`. This is the general form of the 13.7M
`Chess_Position` entities on the wrong ladder — nothing flagged it because nothing could.

Note the asymmetry against the system's own design virtue: the evidence layer *can* abstain
(zero is reachable); the geometry layer cannot.

### 2.5 The detector already exists and is never read

`extension/laplace_substrate/sql/schema/tables/physicalities.sql.in:40`:

```sql
radius_origin double precision GENERATED ALWAYS AS (
    public.laplace_radius_origin(coord)
)
```

`physicalities_radius_btree.sql.in` (index removed 2026-07-28):

> *"radius_origin is a STORED generated column that the SQL surface only ever PROJECTS
> (structural_neighbors_of selects it); it is never a predicate, a join key, or an ORDER BY
> anywhere in the extension… MEASURED 0 scans against 99,152,023"*

The resultant length of a set of unit vectors is the standard **concentration measure** from
directional statistics: ≈1 for a tight cluster (confident placement), ≈0 for a dispersed one
(meaningless placement). That is exactly the placement-confidence signal §2.4 says is
missing — computed, stored on ~99M rows, and never queried as a predicate.

Dropping the index was the right call for an unused index. The reason it is unused is that
nothing asks the question.

**RESOLVED 2026-08-12 — it is a free win.** `coord` is stored **pre**-normalization, so
`radius_origin` carries live signal rather than a constant 1. Confirmed two ways:
`MODEL_INGESTION_DESIGN.md` records `‖cat‖ = 0.2416`, and a live substrate reproduces it
exactly (`0.241568559141`). Measured distribution now available permanently via
`ops.placement_health()` — see §7.

### 2.6 Hilbert locality runs one direction only

`hilbert4d` is the load-bearing trick of the storage thesis: it turns a 4D coordinate into
one sortable 128-bit integer, so a glome lives in a btree range scan instead of a spatial
index. That is why "it's just Postgres" holds.

But: close on the curve ⇒ close in space (guaranteed); **close in space ⇏ close on the
curve.** Every space-filling curve has seams where it folds. Hilbert minimizes this better
than Z-order but cannot remove it, and at d=4 a spatial neighborhood can shatter into many
disjoint index ranges.

So a single-band beam has **structural false negatives** — geometry that is genuinely near,
unreachable by the scan meant to nominate it. Relevant to spec 36's *"In every case the
evidence was resident and unqueried."* Some of that may be a seam, not a missing stage.

---

## 3. The self-witness loop

Implemented and intentional:

- `converse/witness_precedes_chain.sql.in:4` — *"generated text's own edges fold into the
  SAME consensus the next walk reads"*
- `SubstrateTools.cs:86` (chat) — *"prompt and reply deposit as witnessed content
  (UserPrompt/Response trust classes) and fold, so the turn is visible to the next walk"*

The substrate cannot be read without being changed. Damping exists — Response/UserPrompt
trust classes are *"outranked by curated sources BY DESIGN"*, and
`laplace_attestation_witness_phi` scales each deposit by trust × rank.

**The open question: damped per deposit ≠ bounded in the limit.** Glicko is additive —
`observation_count` and `sum_score_fp1e9` accumulate, and `rd` **tightens** with witness
count. Repetition can outrun low trust. The only opposing force is decay:

`inference/laplace_decay.sql.in`:

```sql
SET rd = LEAST((rd / p_decay_factor)::bigint, consensus.glicko2_initial_rd())
```

`rd / 0.95` grows rd, ceilinged at initial. So two opposed forces act on the same column:
**self-witness tightens `rd`, decay widens it.** Which wins is a stability condition with an
actual numeric threshold, and it has not been computed.

Risk if self-witness wins: the substrate grows confident in a claim because it keeps hearing
itself say it.

---

## 4. Claims examined

### "All knowledge has a coordinate within a finite space"

This is an **addressing scheme**, not a result about knowledge. Any countable set embeds
densely in any perfect Polish space — the unit interval does the same job via binary
expansion. What Super-Fibonacci adds over a naive injection is **low discrepancy**: points
spread instead of clumping. That is a real engineering win (locality, beam coherence,
uniform density, sane Hilbert banding) and carries no semantic content.

The system's own law already says so: *"distance on S3 != meaning."* Every entity having a
coordinate is true in the same sense as every entity having a blake3 hash.

Also: the window is bounded by `Hilbert128` — 2^128 addressable cells. Enormous, but finite,
and it is the bound that actually governs.

### Laplace vs Heisenberg

The naming is a coherent answer to the uncertainty objection. Laplace held determinism *and*
founded Bayesian probability without contradiction: probability is the calculus of **our
ignorance**, not the world's. So `rd` is epistemic, not ontic — there is no ħ and no
conjugate bound. Fully determined state, incomplete observation.

Where it does bite: **the demon must stand outside the system.** This one does not — §3.
A demon that writes to the tape it reads is a particle in the universe it was meant to
observe.

---

## 5. Live incident — substrate down

State at time of writing:

- `laplace-postgresql.service` — **failed** (PG 18.3, `/opt/laplace/pgsql-18`, data on
  `/opt/laplace/pgdata`). Restart loop gave up at counter 6.
- `laplace-api.service` and `laplace-mcp` — **still running**, serving a dead database.
  `mcp__laplace__health` returns `substrate unavailable`.

Failure, from `/opt/laplace/pgsql-18/log/postgresql-2026-08-11_224741.log`:

```
LOG:    database system was shut down at 2026-08-11 21:54:36 UTC
LOG:    invalid checkpoint record
PANIC:  could not locate a valid checkpoint record at 1ABC/9A1095B0
LOG:    startup process (PID 8228) was terminated by signal 6: Aborted
```

The control file reports a **clean** shutdown, then cannot find the checkpoint record it
needs. That is a WAL problem, not a config problem.

Ruled out:

- **Not disk pressure** — `/opt/laplace` 209G free, `/opt/laplace/pgdata` 843G free.
- **Not an unmounted volume** — both `vg--raid-lv--laplace` and `vg--data-lv--postgres` are
  mounted.
- **Not the huge-pages work** — 16,870 pages reserved, all free; PG panics before taking
  them.

Not yet inspected (needs root; data dir is `postgres`-owned):

- `pg_controldata` — latest checkpoint / REDO location / prior checkpoint / timeline
- whether `pg_wal` contains the segment covering `1ABC/9A1095B0`, and whether `pg_wal` is a
  symlink to a volume that is not mounted

**Caution:** `pg_basebackup@.timer` and `pg_dump@.timer` are both **disabled**. Confirm what
backup actually exists before any lossy recovery step. `pg_resetwal` discards data and
should not be reached for until `pg_wal` contents are known.

---

## 6. Open queue

1. **Recover the cluster** (§5). Inspect `pg_controldata` + `pg_wal` before anything lossy.
2. **Check whether `coord` is normalized for composed entities** (§2.5). One query. Decides
   whether the placement-confidence detector is free or absent.
3. **Composed placement** (§2.2–2.4) — Fréchet vs Euclidean, and an explicit failure signal
   for origin collapse instead of a silently valid-looking unit vector.
4. **Compute the self-witness stability threshold** (§3) — does repetition outrun decay?
5. **Build the composed forward pass.** Per `docs/archive/specs-v1/36_Laplace_Forward_Pass.md`:
   S1/S3/S7/S8 unbuilt, S2 degenerate, `chat()` runs `{S0, S2-degenerate, S4, template}` and
   stops. Order: skeleton first (all ten stages, typed frontier threaded, unbuilt stages fail
   loudly rather than falling through to the template) → de-degenerate S2 → S3 → S1 → S7/S8 →
   **RD-as-temperature at SELECT** (spec 36 line 74, unbuilt).
6. ~~**Docs currency.** Specs 36 and 37 … sit in `docs/archive/specs-v1/` … Promote them to
   `docs/specs/`.~~ **WRONG — WITHDRAWN 2026-08-12.** Specs 36 and 37 were already in
   `docs/specs/` before this review was written. I read the `docs/archive/specs-v1/` copies
   and treated them as current; the live versions differ by **594 and 516 lines**
   respectively. Every forward-pass claim in this document sourced to spec 36 (`S1/S3/S7/S8
   unbuilt`, `chat() runs {S0, S2-degenerate, S4, template}`) came from the superseded copy
   and must be re-checked against `docs/specs/36_Laplace_Forward_Pass.md` before being acted
   on. A `docs/STATE.md` would still help; promoting already-promoted specs would not.

---

---

## 7. Measured on a live substrate — 2026-08-12

Sections 2.2–2.5 above were inferences from source. They are now measurements. State at
time of reading: tier 0 = **1,114,240** (P1 satisfied), 7.03M entities, 10.6M attestations,
9.0M consensus, OMW mid-ingest.

**Tier 0 is immaculate.** 32,421 sampled, `radius_origin` = 1.000000 for every row, 0
off-sphere; independently confirmed via `ST_X²+ST_Y²+ST_Z²+ST_M²` = 1.000000000000 at both
min and max. The hydration path is uncontaminated — composed placement consumes real child
coords (`NgramTrajectory.cs` builds `coords` from `c.X/.Y/.Z/.M` and `childIds` from `c.Id`
in one loop, feeding `Math4d.Centroid` and `Trajectory.Build` separately). The type-blind
`pg_laplace_centroid_4d`, which would happily average a packed LINESTRING, has **no
production callers**.

**Composed placement collapses, and it is script-dependent.** Exact over the whole table
(split on `physicalities.n_constituents`, so no join to `entities`; `radius_origin` is
STORED, so 15.6M rows aggregate in 1.3 s — sampling is unnecessary and was a mistake in the
first cut of this section):

| cohort | rows | min r | avg r | under 0.1 | digits lost | fp32 digits left |
|---|---|---|---|---|---|---|
| tier 0 (on spiral) | 1,114,240 | 1.000000 | 1.000000 | 0 | 0.00 | 7.22 |
| composed (centroid) | 14,481,064 | **0.003148** | 0.453797 | **241,015 (1.66%)** | **2.50** | **4.72** |

Sampled per-tier detail (`ops.placement_health_by_tier()`): tier 1 avg 0.528 / 4.57% under
0.1, tier 2 avg 0.373, tier 3 avg 0.450. Tier 1 is the worst cohort proportionally.

"Digits lost" is `-log10(r)`: constituents are unit vectors, so it is the decimal precision
destroyed by cancellation in forming the mean. fp64 carries ~15.95 digits and retains ~13.5;
fp32 carries ~7.22 and retains ~4.7, and less for the 1,184 rows under r = 0.02.

`ops.placement_unreliable()` shows the worst offenders are overwhelmingly **non-Latin** —
`化学受容器`, `spărgător`, long Japanese glosses. This follows from DUCET-as-latitude:
multi-script content draws constituents from widely separated collation bands, so the
Euclidean mean nearly cancels. **Karcher is therefore not a correctness nicety — the
multilingual substrate is being placed measurably worse than the English one.**

**Identity is uniform; geometry is not.** `blake3_byte0` CV **5.0%** (theoretical ideal for
this n ≈ 4.6%) against `hilbert_byte0` CV **277%**, 240/256 buckets occupied. `HASH(entity_id)`
partitions spread at **0.55%**. A counterfactual 64-band RANGE on the Hilbert leading byte
peaks at **9.29%** against a uniform 1.56% — 6× over, disqualifying for partitioning but far
from the earlier recollection of ~60%. The decisive argument for HASH is not any single skew
figure but that hash is **composition-invariant**: Hilbert skew moves with whatever is
ingested next, because a volume-filling curve over a 3-manifold shell bands regardless.

**DUCET is proven, not assumed.** `x²+y² = s/N` recovers the collation rank exactly, with
`N = laplace.atom_window() = 1,114,112` (**not** the 1,114,240 tier-0 row count, which
includes sentinels). Ranks land on exact half-integers, pinning `s = i+0.5`. Three
signatures impossible under codepoint order: `a`=12142.5 precedes `A`=12207.5; `à á â` sit
at 12143.5/12144.5/12145.5, consecutive with their base letter rather than at codepoints
224/225/226; and the earliest spiral positions are variation selectors and tag characters
(U+FE05, U+E0027, U+E0100) — DUCET's completely-ignorable class.

**The metric ladder, run rather than quoted** (cat/act, realized curves):
`coord_distance` 0 · `hilbert` collides · `hausdorff` **exactly 0** · `frechet` 1.779 ·
`merkle_id` differs. Three order-blind rungs collide, two order-bearing rungs separate.

**The summation-order defect is mechanistically proven.** Feeding the same three tier-0
points to `laplace_centroid_4d` in all six orders yields groups determined solely by the
*final* addend — floating-point addition is commutative but not associative. Every pair
differing by a swap of the first two addends is bit-identical (3/3), and the groups
reproduce the stored rows bit-for-bit (`cat`/`act` → `…e66b21e76dd7`, `tac` →
`…d12cad3b8fdb`). Fix: sum constituents in canonical id order, or Kahan/pairwise. Costs one
`ORDER BY`, changes no math, and makes order-blindness a guarantee instead of something
Hilbert quantization happens to rescue.

**`realize.render_gaps` is 11/15 correct behaviour, 4/15 a real problem.** Of 2,177
unresolvable tier-0 entities: **2,048 surrogates** (U+D800–DFFF — not characters), **1**
(U+0000 — a PostgreSQL `text` limitation, not a Unicode one), **128 sentinels** (by design).
Exact accounting. Noncharacters (U+FDD0–FDEF, U+xFFFE/xFFFF) are all present and resolvable.
The remaining 4 gaps are ids referenced by attestations with **no row in `laplace.entities`**
— one of them 203 times, three with a physicality but no entity. Sampled dangling rate: 0
subjects, 6 objects per 125,951 consensus rows (~0.005%). Re-check after OMW settles; some
may be in-flight ordering.

**Label coverage is 100% at tiers 0/2/3/4** — zero hex fallbacks, zero nulls — but at ~42 ms
per scalar `converse.label_or_hex` call. The `_batch` variants exist for exactly this reason.

### Permanent artifacts added

| object | purpose |
|---|---|
| `ops.ducet_rank(geometry)` | collation rank from geometry; no `allkeys.txt` at runtime |
| `ops.ducet_ordered(from, limit)` | atoms in DUCET order; cheap rank filter first, resolution last |
| `ops.placement_health(pct, thresh)` | the §7 table, on demand |
| `ops.placement_unreliable(thresh, limit)` | the offending rows, one batched label call |
| `ops.metric_ladder(a, b)` / `_words(a, b)` | all five rungs over **realized curves** |
| `physicalities_ducet_rank_btree` | makes a collation range an index scan (**apply off-ingest**) |

---

## 8. Corrections

Following `MODEL_LANE_AUDIT_2026-08-11.md`'s convention. Every item below is something this
document or its author asserted and then had to withdraw.

1. **Read archived specs as current** (§6 above). The most consequential error here.
2. **"Hilbert is 1000× skewed"** — min-bucket against peak. Honest figures: 21× at byte
   granularity, 6× at band granularity.
3. **"Interior points cluster at box-centre byte 0x80"** — predicted from quantization;
   measured 0.6–2.7% per tier. The Hilbert transpose scatters them. Theory wrong.
4. **Predicted three distinct centroid values across six permutations** — there are two.
   Which groupings diverge is a ULP coincidence, not structural.
5. **Fed packed trajectories to `laplace_hausdorff_4d`/`laplace_frechet_4d`** in the first cut
   of `ops.metric_ladder` — the exact violation the placement law forbids, and one commit
   `2537fcd7` had already fixed elsewhere. Symptom: Hausdorff 4.44e-16 instead of 0.
   Corrected to `structural.entity_curve`.
6. **"Content-addressed ids beat foreign keys"** — half true. Blake3 guarantees that *if* a
   row exists it is the right row; it guarantees nothing about **existence**. The 4 dangling
   references above are what that gap looks like.
7. **Claimed noncharacters are excluded** — all 66 are present and resolvable.
8. **Recommended BRIN on `last_observed_at`** — `attestations_last_observed_brin` already existed.
9. **Said 4D Fréchet would need building** — `laplace_frechet_4d` and `laplace_hausdorff_4d`
   already shipped.
10. **"100% label coverage"** — tier 1 never appeared in the 0.05% sample and does contain a
    hex fallback.
11. **First cut of the `ops.*` functions called expensive resolvers inside CTEs and
    projections** — `codepoint_for_id` three times per row across 1.1M rows, every metric
    computed twice. Rewritten to filter cheap, resolve last, evaluate once.
12. **Conflated numeric WIDTH with reduction ORDER on the fp32 question.** These are
    orthogonal and only one of them is about fp32. *Order*: a GPU reduction is
    nondeterministic at any width (CUDA defaults to `-fmad=true`; block reduction order is
    undefined), while a CPU fp32 reduction in fixed order is bit-reproducible — which is
    what `-fno-fast-math -ffp-contract=off` buys. *Width*: cancellation at r = 0.003148
    destroys ~2.5 decimal digits, which fp64 absorbs and fp32 largely does not. Width
    governs the placement path; order governs anything feeding a hash, `coord`,
    `hilbert_index` or `trajectory`. Credit to the concurrent fp32/fp64 audit for the
    separation.
13. **Sampled a measurement that should have been exact.** `radius_origin` is a STORED
    generated column, so the full 15.6M-row aggregate costs 1.3 s; the `TABLESAMPLE` in the
    first cut of `ops.placement_health` bought nothing and understated the minimum by 3.7×
    (0.0117 sampled vs 0.003148 exact). Also joined `entities` for a tier split that
    `physicalities.n_constituents` already provides.
15. **Read the tier-transition matrix as damage when it was the law working.** Measured
    `word → codepoint` at 96.3%, `document → sentence` at 6.2%, and 30.6% of
    `sentence → codepoint` edges carrying content letters, and called the decomposers
    broken. **Tier is a floor, not a rung in a containment ladder.** Same content = same
    hash means a one-codepoint grapheme *is* the codepoint and a one-word sentence *is*
    the word — there is nothing separate to record, and `TextEntityBuilder`'s
    `CollapseIndex` implements exactly that. The one-child collapse exists *because*
    length-1 trajectories once duplicated ~1,100 physicality rows. Every "skip" in that
    matrix is deduplication working. `"So?"` and `"No."` are word + punctuation-codepoint,
    which is the `sentence → codepoint` row I called a defect.
16. **Claimed UAX #29 grapheme segmentation was never wired into text decomposition.**
    False. `text_decomposer.c:43` calls `laplace_grapheme_floor_build`, which calls
    `laplace_grapheme_break_next`, and `grapheme_floor.c:80` emits a tier-1 node for every
    cluster unconditionally. The ladder is built correctly; the collapse is applied at
    persistence. The UI duplicates tier 0 and tier 1 because `ExploreDecomposeService`
    renders the *uncollapsed* live tree with no database — that is a display bug, not a
    substrate defect.
17. **Ordered the Karcher canonical sort with `memcmp` over raw double bytes.** A valid
    total order on any one host, but byte order is endianness-dependent, so two hosts of
    different endianness would sort constituents differently and derive different
    placements from identical inputs — architecture-stable content addressing requires
    otherwise. Replaced with component-wise IEEE-754 comparison (`laplace_dbl_cmp`), NaN
    ordered last so the comparator remains a valid strict weak ordering. Caught by the
    concurrent audit.

---

*Not checked against `docs/DOCUMENTATION_GOVERNANCE.md` — filed following the
`MODEL_LANE_AUDIT_2026-08-11.md` dated-audit convention.*
