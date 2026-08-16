# Model lane audit — 2026-08-11

Every claim below carries the command that verifies it. Nothing here is asserted
from a code comment: the comments in this lane were wrong three separate times
during the audit (documented in "Corrections" at the bottom), so prose is evidence
of intent at best. Re-run the commands before trusting any line of this file.

Substrate: `laplace` on `/opt/laplace/pgdata` (vg-data/lv-postgres).
Models ingested during the audit: `sentence-transformers/all-MiniLM-L6-v2`,
`TinyLlama/TinyLlama-1.1B-Chat-v1.0`.

---

## 1. What the two model passes actually cost

`LAPLACE_MODEL_PLANES` selects between them (`ModelTokenEdgeETL.ResolvePlanesMode`).

| | `structure` (default) | `factors` |
|---|---|---|
| attestations (TinyLlama / MiniLM) | **177,959** | 775 |
| new entities | 8,603 | 301 |
| dedup on apply | 23,017 of 28,123 already present (82%) | none |
| consensus folded | 38,190 cells / 13 type lanes | 0 |
| provenance | source per attestation | payload only |
| storage | megabytes | **81 GB observed, 210 GB projected** |
| runtime | 252 s | 6+ h, ENOSPC, cluster restart |

`structure` scrapes declared checkpoint structure (config, tensor byte-ranges,
tokenizer vocab, BPE merges, `TOKEN_MAPS_TO`, `CONTAINS`/`PRECEDES`) and resolves
it against entities that already exist. `factors` computes per-token activations
and stores them as PostGIS payload.

```bash
# reproduce both passes
./scripts/laplace ingest model /vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0
LAPLACE_MODEL_PLANES=factors ./scripts/laplace ingest model <same path>
```

## 2. The 210 GB, derived and measured

Storage is `≈ 5.33 × V × d × (H+2) × L`. It scales with vocab × dim × heads ×
layers, **not** with parameter count. The `(H+2)` term is the defect: OV slices
are deposited at full model dim **per head** (`ModelTokenEdgeETL`
`BuildOvDeposits`: `OVh = new double[n*d]`, then `BuildDeposit(..., n, d, ...)`),
so each layer stores `H × d` floats per token where the layer's actual output into
the residual is `d`.

Measured, one slice:

```sql
SELECT source_dim, n_constituents, ST_NPoints(trajectory),
       pg_size_pretty(length(trajectory::bytea)::bigint)
FROM laplace.physicalities
WHERE trajectory IS NOT NULL AND n_constituents = 26622
ORDER BY length(trajectory::bytea) DESC LIMIT 1;
-- 2048 | 26622 | 9131347 | 279 MB      (9131347 = 1 + 26622*343)
```

Totals:

```sql
SELECT count(*), sum(ST_NPoints(trajectory)),
       pg_size_pretty(sum(length(trajectory::bytea))::bigint)
FROM laplace.physicalities
WHERE trajectory IS NOT NULL AND n_constituents IN (26622, 27852);
-- 2207 rows | 2,730,293,241 vertices | 81 GB
```

Note the axis: **2,207 rows is not the problem.** Each row spans the whole
vocabulary. Projection across the local library is 23.1 TB from 0.24 TB of
checkpoints; `Qwen2.5-Coder-14B` alone is 7.01 TB. MoE is worse — for a
160-expert model the expert term is ~71% of each layer, because every expert
receives its own full-`d` slice. `Qwen3-Coder-480B` projects to 57.8 TB.

Blowup runs *backwards* from intuition (`5.33·V·d·(H+2)·L` vs parameter count):
`deepseek-coder-33b` is 55×, `jina-reranker-v3` at 1.2 GB is **304×**. Small
hidden dim with a large vocabulary is the worst case.

## 3. Identity and provenance are correct — do not "fix" them

`PhysicalityId` is `H(entityId, type)` and deliberately source-independent:

> `(entityId, type)` ONLY; coord/trajectory are stored as payload but never enter
> the id. Hashing the float geometry made identity fragile to sub-ULP.
> — `app/Laplace.Substrate/Crud/PhysicalityId.cs`

**Adding a `source_id` column to `laplace.physicalities` would break
`same content = same hash`** by forking one content into one row per witness.
Provenance is *associated*, not embedded: `entities.first_observed_by` plus the
`APPEARS_IN` attestation, which carries `source_id`.

```sql
-- dedup working as designed (from the ingest log):
--   APPLY_PRESENT_SKIPPED entities=23017 physicalities=23053
SELECT count(*) FROM laplace.attestations
WHERE type_id = laplace.relation_type_id('APPEARS_IN')
  AND source_id = decode('18a889c42ac64043c7de86d1dae48446','hex');
-- 554   (TinyLlama; MiniLM = 223)
```

Float payload is excluded from identity **by design**, so measuring whether
trajectory bytes dedup answers a question the architecture already settled.

## 4. `trajectory` accepts any geometry — the lane writes one LineString

```sql
SELECT format_type(atttypid, atttypmod) FROM pg_attribute
WHERE attrelid='laplace.physicalities'::regclass AND attname='trajectory';
-- geometry(GeometryZM)          <- generic, no CHECK constraint

SELECT ST_GeometryType(trajectory), count(*) FROM laplace.physicalities
WHERE trajectory IS NOT NULL GROUP BY 1;
-- ST_LineString 20,206,514 | ST_Point 9,653
```

The column holds any ZM geometry. `MULTILINESTRING ZM` with one component per
token would be the same bytes while making each token's run independently
addressable — which is what the ~450 ms linear varlena scan admitted in
`model_factor.sql.in`'s own header exists because of.

## 5. The vertex container already supports sparse encoding; the writer bypasses it

`engine/core/src/mantissa.c`, `laplace_factor_unpack_vertex`: a vertex
self-describes its value count (`VFLAG_FCOUNT`, 1–6) and the payload carries a
`run_length` field. `FactorTrajectory.Pack` writes fixed `ceil(dim/6)` vertices
per token with all six slots filled, no threshold and no runs, because
`FactorTrajectory`'s own comment wants `vertex = 1 + t*stride` constant-time
addressing. That trade was never collected: the v1 reader is a linear scan.

Values are packed into *mantissa slots* of the four doubles, not stored as
float32 — decoding a trajectory as raw float32 produces `3.4e38` garbage. Use the
real unpacker:

```bash
gcc -O2 -Iengine/core/include -o dump dump.c engine/core/src/mantissa.c
# harness calling laplace_factor_unpack_vertex per 32-byte vertex
```

Decoded, one d=2048 slice (1,045,517 factor vertices, 3,058 testimony vertices,
0 unclassified): max `0.0319`, median `0.000336`, 92.15% of values ≤ `0.001`.
L1 mass is nearly flat — top 1% carries 4.8%, top 25% carries 55.5%. So there is
**no sparse knee to threshold at** in the stored slice; a θ-cut on this payload
saves nothing. The waste is redundancy (`H×d` per layer) and precision, not
occupancy.

## 6. ~~The traversal engine is dead code~~ — **FALSE, RETRACTED 2026-08-16**

> **This section is wrong on both clauses. Do not cite it.** Verified at HEAD:
>
> - `extension/laplace_substrate/src/astar_path.c:280,289,298` calls
>   `astar_open` / `astar_next` / `astar_close`. It is reached from
>   `NpgsqlSubstrateReads.cs:2205,2213,2225` → `converse.astar_path`
>   (`cascade/astar_path.sql.in:2`) → `astar_path_raw.sql.in:10`
>   `AS 'MODULE_PATHNAME', 'pg_laplace_astar_path'`.
> - `astar_path.c:113-115` — `edge_cost()` **is**
>   `laplace_walk_edge_weight(rating, rd, witness_count, kappa)`, consumed at
>   `:158` as `out[r].cost`, with an admissible heuristic closure at `:170-186`.
> - `generate_walk.c` and `astar_path.c` both exist and were added **2026-06-05**
>   and **2026-06-06** — two months before this audit.
> - The cited path `glicko2.h:91` does not resolve; the file is at
>   `engine/core/include/laplace/core/glicko2.h`.
>
> **The reproduction command below was published as a receipt, not executed.**
> Running it verbatim returns `astar_path_raw.sql.in`, `astar_path.sql.in`,
> `laplace_astar_path.sql.in`, `NpgsqlSubstrateReads.cs:2205-2225` and
> `consensus_adjacency.sql.in:42` in its first eight lines.
>
> `.scratchpad/38_Operation_ISA_Audit.md` §2.1 — dated 2026-07-27, two weeks
> *before* this audit — already cited `astar_path.c:267` as a live caller of
> `spi_fetch_rd_kappa()`. This section contradicted a document already in the tree.
>
> Consequence: `docs/plan/MODEL_INGESTION_DESIGN.md` order-of-work **item 6**,
> which was derived entirely from this section, is retracted. No traversal engine
> needs to be built.

### Original text, retained as the record of the error

```bash
grep -rn 'astar' --include='*.c' --include='*.cpp' --include='*.cs' \
  --include='*.in' engine/ app/ extension/ | grep -v 'astar.cpp:\|astar.h:'
# only engine/core/tests/test_astar.cpp — no production caller, no SQL function
```

`astar_open` / `astar_next` / `astar_close` are implemented in
`engine/core/src/astar.cpp` and called by nothing. `glicko2.h:91` cites
`generate_walk.c`'s beam scorer and `astar_path.c`'s edge_cost — **neither file
exists.**

What runs instead:

```sql
SELECT p.proname,
  (prosrc ~* 'WITH RECURSIVE') AS recursive_cte,
  (prosrc ~* 'heuristic|admissible|frontier|priority queue|open set') AS search_terms,
  (length(prosrc)-length(replace(upper(prosrc),'ORDER BY','')))/8 AS order_bys
FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE (n.nspname,p.proname) IN (('generation','walk_batch'),('converse','chat'));
-- walk_batch | f | f | 14
-- chat       | f | t | 17
```

`converse.walk` is a 183-char wrapper: `SELECT b.reply FROM
generation.walk_batch(...) LIMIT 1`. No recursion, no frontier, no edge cost.
The Glicko-complete read side *does* exist —
`laplace_walk_edge_weight(rating, rd, witness_count, kappa)` in `glicko2.h`,
marked "ratified and live" — and `walk_batch` does not use it as a path cost.

## 7. The bound that makes model ingest tractable

A weight is a **vote's significance on an edge that already exists**, not content.
So the cost is bounded by existing edges among the surfaces the model's vocabulary
covers — not by `V²`.

```sql
CREATE TEMP TABLE tl_ents AS
SELECT DISTINCT object_id AS id FROM laplace.attestations
WHERE type_id = laplace.relation_type_id('TOKEN_MAPS_TO')
  AND source_id = decode('18a889c42ac64043c7de86d1dae48446','hex');

SELECT count(*) FROM tl_ents;                                        -- 26,622
SELECT count(*) FROM laplace.consensus c
  JOIN tl_ents s ON s.id=c.subject_id JOIN tl_ents o ON o.id=c.object_id; -- 55,971
SELECT count(*) FROM laplace.consensus c
  JOIN tl_ents s ON s.id=c.subject_id;                               -- 336,795
```

| | dense trajectories | weight-as-ballot |
|---|---|---|
| new consensus rows | 2,207 physicalities | **0** (cells already exist) |
| new attestation rows | 775 | 55,971 |
| storage | **210 GB** | **~9.5 MB** |
| 15-model library | 23.1 TB | **~150 MB** |
| adjudication | none | Glicko, scaled by `AIModelProbe` trust |

**22,000× smaller.** For contrast, a θ-cut over all `V²` pairs is *not* cheaper —
measured on TinyLlama L0 q/k head 0 (4,000-token sample, arena `RMS|m| = 0.0045`),
survival is 23.1% at 1×RMS and 0.48% at 4×RMS, i.e. 236M and 4.9M edges per
circuit. At ~170 B per attestation row that is 40 GB and 0.84 GB per circuit —
worse than the 0.279 GB slice until θ ≳ 5–6×RMS. Enumerating pairs reintroduces
the `V²` materialization the design rejected. Voting on existing edges does not.

### What exists to build it

- `NativeAttestation.CategoricalResolved(subject, typeId, obj, sourceId, contextId, double witnessWeight, …)` — the sink. The model lane calls it hardcoded to `witnessWeight: 1.0`.
- `laplace_walk_edge_weight(rating, rd, witness_count, kappa)` — Glicko-complete read side.
- `engine/core/src/glicko2.c` — the fold.
- `bilinear_edges_tile` (`engine/dynamics/src/bilinear_edges.cpp`) — the contraction, still compiled and tested.

### Deleted, recoverable

Commit `7022bbca` ("Manual user commit to clear stage", 2026-06-04) removed
`ModelCircuitEdges.cs` (267 lines) and its 195-line test, and added
`ModelTableETL.cs` (+311) in the same commit. `KindRegistry` and
`AttestationFactory.CreateWeighted` — the `magnitude`+`floor` → score conversion
and the `kind_rank × source_trust → φ` routing — exist nowhere in the tree.

```bash
git show '7022bbca^:app/Laplace.Decomposers.Model/ModelCircuitEdges.cs'
git show '7022bbca^:app/Laplace.Decomposers.Abstractions/KindRegistry.cs'
```

Read the bodies, not the headers. `ModelCircuitEdges`' header claims "the ONLY cut
is θ"; the body also drops self-edges and any token without a content entity, and
its `cap` throws rather than truncating. Its emitted shape is
`token_i →(kind) token_j` — pairwise, which is a sparse weight matrix with
ratings, not the collection-valued form.

## 8. Open, not measured

- Whether restricting votes to already-existing edges discards couplings the model knows that no source has witnessed. Admitting novel edges is a knob (which θ, which kinds), not an unbounded cost — 10× more is 100 MB — but the setting is unknown.
- The cross-circuit fold ratio: how much the same `(token_i, token_j, kind)` coupling recurs across the 1,518 circuits. Attestations fold into one cell with an incrementing `observation_count`; payload cannot. This ratio is the difference between "edges win" and "edges lose" and was not measured.
- Whether the `factors` slice payload encodes separable signal at all. The decoded distribution is near-Gaussian with max 0.0319 and no concentration, which is closer to a random projection than to structured circuit output. n=1 slice.

## Corrections — claims made during this audit that were wrong

Recorded so they are not re-derived. Each was stated confidently and corrected
only after being challenged; each was fixed by reading a file, not by reasoning.

1. **`HAS_CAPITAL`** — invented a relation that appears in no ingested source, to make a lookup tidy. There is no such relation and modelling a question as `subject →RELATION→ ?` is a dictionary GET, not composition.
2. **`ModelCircuitEdges` header quoted as evidence** of what the code did, without reading the body.
3. **`KindRegistry` comment quoted the same way**, one message later.
4. **Two sparsity distributions published from bad decodes** — raw float32 over the blob, then a guessed stride. Both produced `max = 3.4e38`. The packing is native (`mantissa.c`).
5. **"25% container waste, 8 bytes padding per vertex"** — wrong mechanism; packing is dense into mantissa slots.
6. **"Provenance is accepted and dropped"** — backwards. Source-independent ids are the dedup mechanism; provenance rides `APPEARS_IN` + `first_observed_by`.
7. **Pricing the edge form over `V²` pairs** — reintroduced the rejected materialization, then costed it.

---

## 9. What a checkpoint actually contains, by tensor role

Measured from the safetensors headers.

```
MiniLM-L6-v2 — 91 MB                    TinyLlama-1.1B — 2,200 MB
  vocab tables   47.7 MB  52.5%           FFN           1,522.5 MB  69.2%
  FFN            28.9 MB  31.9%           vocab tables    262.1 MB  11.9%
  VO              7.1 MB   7.8%           QK              207.6 MB   9.4%
  QK              7.1 MB   7.8%           VO              207.6 MB   9.4%
  norms            0.0 MB   0.0%          norms             0.2 MB   0.0%
```

**52.5% of MiniLM is a lookup table for 30,522 tokens the substrate already holds
as entities** -- the same entities, deduped at 82% on contact during the ingest.
That half contributes no content by construction. Only `norms` are non-relational
(scalers); every other group is `[tokens] rel [tokens]` with a strength: QK is
which tokens couple, VO is what a token contributes when coupled, FFN is a
key-value memory (up-projection keys, down-projection values).

## 10. Sparsity: measured, and mostly inconclusive in this basis

TinyLlama layer 0 MLP (SwiGLU), 3-4k sampled tokens, embeddings through
RMSNorm as probe input.

| measurement | result |
|---|---|
| pooled post-SwiGLU, top 1% of values | 13.7% of mass |
| per token, neurons for 90% of that token's mass | median 2,799 of 5,632 (49.7%) |
| keep top 30 / 50 / 70 / 90% of neurons by peak | retains 52.5 / 70.4 / 84.6 / 95.9% of mass |
| neurons whose peak over 4,000 tokens never exceeds 0.001 | **0 of 5,632** |

No dead capacity at this layer, and a near-linear keep-vs-mass curve with no
elbow. So the quiet neurons differ per token -- sparse *activation*, not idle
capacity.

Three reasons this does not settle prunability, recorded so the measurement is
not over-read later:

1. **SiLU cannot emit zero.** Classic 90-99% activation-sparsity results are for
   ReLU, which hard-zeroes. A smooth gate manufactures density the way softmax
   does at the output; the 49.7% is partly the activation function, not the
   knowledge.
2. **Wrong basis.** Superposition/SAE results say *neurons* are dense while
   *features* are sparse in a learned overcomplete dictionary. Neuron-basis
   measurement is where the effect is predicted to be invisible.
3. **Wrong unit.** Pruning's 40-90% figures are for individual *weights*; a
   neuron survives on 10% of its incoming weights and still peaks. Activation
   mass is also not functional contribution -- pruning measures task loss.

Layer 0 with embedding-only input is additionally the most diffuse layer in the
stack.

## 11. Import and export disagree about what is derivable

Export treats these operators as low-rank and says so as law: `BasisRank = 256`,
and `FoundryCommands.cs:1221` -- "Eckart-Young-optimal rank-d error — the
synthesized floor IS the table." A runnable model is built from that basis.

Import applies derivability exactly one level too shallow
(`ModelTokenEdgeETL.cs:127`): it refuses to materialize the `V²` pairs, then
fully materializes the `[V x dim]` field those pairs would be derived from --
the field the export has already established is rank-256.

| same slice | values | bytes |
|---|---|---|
| stored dense (26,622 x 2048) | 54.5 M | 279 MB |
| rank-256 factorization | 7.3 M | ~38 MB |

7.4x by the repo's own optimality argument, before dedup or canonicalization. If
rank-256 suffices to *reconstitute* a model, it suffices to *record* one.

## 12. The acceptance criterion is marginal, not fidelity

Every measurement above asks "how much of this model can be captured," which is
the isolation question -- completeness, which no system gets. The question the
substrate poses is what a checkpoint **adds** to what is already witnessed.

Consequences for what a correct ingest looks like:

- Agreement with an existing edge costs an `observation_count` increment, not a row.
- The vocab table (52.5% of MiniLM) adds nothing: those entities exist.
- **The second model ingested should cost less than the first, and the tenth less
  again**, as the graph already holds what is true. Cost growing linearly per
  model is the signature of recording representations rather than adding
  knowledge -- which is what 87.4 GB across two checkpoints is.

Noise is not removed by a threshold. A real coupling accumulates witnesses across
heads, layers, independent models, and the curated sources; gradient jitter does
not, because one model's jitter is uncorrelated with another's. Glicko `rd` is
the instrument, and the floor is *discovered* rather than configured.

Canonicalization is not optional: `same content = same hash` requires that two
functionally identical models with permuted neurons produce the same nodes, so
gauge freedom must be removed at intake or content-addressing cannot hold. Open:
what the canonical form is for an attention head (permutation is easy, rotation
is not), and whether "would never fire" is decidable analytically (reachability
and always-closed gates are; arbitrary couplings likely are not).
