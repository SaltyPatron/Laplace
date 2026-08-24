# Laplace — architecture as built

Every statement here is traceable to a file in this tree, cited inline. Counts come
from `docs/INVENTORY.md`, which `scripts/docs-inventory.py` regenerates and CI gates.
Where the code and this document disagree, the code is right and this document is the
thing to fix.

---

## 1. The record

One shape carries every fact from every source: a subject, a relation type, an object,
who said it, and how it came out.

`AttestationRow` — `app/Laplace.Substrate/Crud/SubstrateChange.cs`:

```csharp
Hash128 SubjectId, TypeId, ObjectId?, SourceId, ContextId?
AttestationOutcome Outcome          // Refute = 0, Draw = 1, Confirm = 2
long ScoreFp1e9, OpponentRdFp1e9, SumScoreFp1e9?
long ObservationCount
```

The outcome domain is three-valued and signed: a source can *refute* a triple, not only
assert or omit it. `ScoreFp1e9` is a continuous fixed-point signal; `OpponentRdFp1e9`
is how the source's own trust enters. Trust is an argument to the rating math, not a
filter applied before or after it.

## 2. Four tables

`extension/laplace_substrate/sql/schema/tables/`.

| Table | Primary key | Partitioning | Role |
|---|---|---|---|
| `entities` | `(id, tier)` | LIST(`tier`) — 0…4 + DEFAULT; tier 2 sub-partitioned HASH(`id`)×8 | one row per distinct content |
| `physicalities` | `(id)` | HASH(`id`) × 64 | geometry: `coord geometry(PointZM)`, `trajectory geometry(GeometryZM)` |
| `attestations` | `(id, type_id, subject_id)` | LIST(`type_id`) | one row per assertion, with provenance |
| `consensus` | `(id, type_id, subject_id)` | LIST(`type_id`) | the fold: `rating`, `rd`, `volatility`, `witness_count` |

Plus `canonical_names`, `highway_mask_dirty`, and four journals
(`ingest_run_journal`, `ingest_flush_journal`, `ingest_file_journal`,
`index_cycle_journal`). The file journal carries per-file resume (#898/#1019).

`highway_mask_dirty` is populated ONLY by the repair verbs (`ops.evict_source`),
which need to CLEAR bits — something the OR-accumulate deposit the ingest uses
(`consensus.highway_mask_deposit`) cannot express. It is empty on a substrate that
has never evicted a source, and that is the correct reading, not a broken queue.
`trajectory_pairs` and `trajectory_pairs_meta` are not part of the current schema;
`drop_retired_content_lane.sql.in` defines the compatibility cleanup.

Three properties do the structural work:

**Identity is content.** `entities.id` is a 16-byte hash of the content. `tier` is a
separate column and is not an input to the hash — so identical content is one id no
matter what tier it was reached at or which source produced it. Two decomposers that
derive the same content produce the same row by collision, with no entity-resolution
step anywhere.

**The fold address is the triple.** `consensus.id = blake3(subject‖type‖object)`, stated
in the header of `consensus.sql.in`. Every witness of a triple, from any source, in any
modality, lands on exactly one consensus row. Because a triple has exactly one relation,
LIST-partitioning by `type_id` files each triple under its relation without touching that
merge invariant.

**Referential integrity is structural, so there are no foreign keys.** `consensus.sql.in`
records the reason: a partitioned `entities` has no unique `(id)` to reference, and
per-row FK validation on 100M-row `COPY`s was pure cost. Ids are content hashes, so a
dangling reference is not expressible.

The partition layout is greenfield — `db-reset` plus reseed is the upgrade path, not
`ALTER`. `IF NOT EXISTS` deliberately leaves a legacy plain table untouched.

## 3. The fold

`engine/core/src/glicko2.c` is Glicko-2 in `int64` fixed point at 1e9
(`LAPLACE_FP_ONE`), with `LAPLACE_FP_RATING_SCALE = 173717800000` (the 173.7178 of the
published algorithm) and `LAPLACE_FP_RD_MAX = 350000000000`. Fixed point is what makes
the fold bit-reproducible across machines.

Folding happens **inside the write**, not after it.
`app/Laplace.Substrate/Crud/Npgsql/ConsensusAccumulatingWriter.cs`:

- Each apply batch dedups its cell deltas in RAM, forwards evidence to the inner writer,
  and dispatches the delta onto per-type fold lanes running `consensus_upsert` — the
  native Glicko fold, server-side, inside each row's lock window.
- **The Glicko rating period is the batch.**
- Lanes are keyed by `type_id`. Consensus is LIST-partitioned by `type_id`, so two types
  never share a row and their lanes run concurrently; a cell has exactly one type, so it
  stays on exactly one FIFO lane. That is what keeps the non-commutative Glicko fold
  deterministic under concurrency.
- Highway-mask deposits are not serialized — OR-accumulate is commutative and idempotent.
- In bulk runs the fold pipelines behind the apply lane (`FoldPipelineDepth = 2`) so
  batch N's fold overlaps batch N+1's probe/COPY. Outside a bulk run the fold is awaited
  inline, because online lanes need read-your-writes consensus.
- Ingest completion is fold completion: no accumulator epochs, no staging tables, no
  terminal fold pass.

There is no batch backfill or rebuild path, by construction.

## 4. Ingest

A decomposer is a pure function from content to a stream of `SubstrateChange` records —
`app/Laplace.Substrate/Abstractions/` (the decomposer roster and counts live in
`docs/INVENTORY.md`, which is generated and CI-gated; counts written here go stale
and then get used as gates — don't). It contains no SQL. The shared spine owns
batching, dedup, the tier descent, the fold, and the COPY.

`SubstrateChange` (`Crud/SubstrateChange.cs`) carries `Entities`, `Physicalities`,
`Attestations`, `IntentStages`, and `TestimonyWalks`.

`Abstractions/IngestPipeline.cs` defines the streaming contract:

- `IMultiFileRecordStream<T>.FilesAsync` yields *lazy* file handles. Enumeration reads
  nothing; each worker opens and streams one file end to end, so parse cost is parallel
  across files and no file is materialized.
- `IIngestRecordHandler<T>` gives three hooks: `TryTrunkShortcircuitAsync` (skip a record
  whose content-addressed root is already present), `CreateDeferredUnit`, and
  `WalkWitness`.
- `ITrunkRootRecord` lets the existence gate bulk-probe known roots and short-circuit
  them without building a deferred unit at all.
- Working-set mode keeps one builder across the record stream and runs an O(tiers)
  existence probe every flush interval — at most five tier rounds per batch — emitting
  one `SubstrateChange` per working set unless a memory valve splits it.

The sequence is: unpack → records → client-side dedup across the working set →
client-side accumulation → one bulk tier descent → COPY of proven-novel rows.

## 5. Relations

`engine/manifest/relation_types.toml` governs the canonical relations and their
aliases (aliases resolve to a canonical and carry no bits of their own; live counts in
`docs/INVENTORY.md`), across 13 salience bands: `mandate`, `definitional`, `taxonomic`,
`equivalence`, `partitive`, `causal`, `oppositional`, `associative`,
`tensor_calculation`, `lexical_glue`, `scalar_valued`, `standards_structural`,
`probationary`.

`scripts/codegen-attestation-law.py` compiles the manifest into generated C, including
the highway bit table. **Bits are an explicit append-only registry** (`bit = N` in the
TOML; ADR 0001 / GH #551): codegen validates and never reassigns — adding a relation
appends a bit, never renumbers peers, and owes **no** reseed. (This corrects an earlier
claim here that bits were alphabetical and additions owed a reseed — the law that
statement described was repealed.)

Dynamic relation families (`DEP_*`, `FEAT_*`, `EDEP_*`) are not in the manifest and land
in the DEFAULT partition.

## 6. Geometry

`physicalities` stores `coord geometry(PointZM)` — a point on S³ — plus a 16-byte
`hilbert_index`, an optional `trajectory geometry(GeometryZM)`, `n_constituents`,
`alignment_residual`, and `source_dim`. `radius_origin` is a generated stored column
computed from the four coordinates.

`hilbert_index` equality lookups are served by an explicit
`physicalities_hilbert_btree`, not by the HASH(`id`)-compatible `(id)` primary key.
`anagrams_of()` proves the index requirement by joining
`w2.hilbert_index = w1.hilbert_index`: anagrams share a letter multiset and therefore
compose to the same coordinate.

Native support in `engine/core/src/`: `super_fibonacci.c` (S³ point placement),
`hilbert4d.c`, `math4d.c`, `mantissa.c` (bit-packing ids/scores/counts through the ZM
columns), `trajectory.c`, `tier_tree.c`, `merkle_dedup.c`, `hash128.c`.

Geometry is an identity, ordering and serialization system. Point proximity is not the
relatedness signal — the consensus rating is. `coord` is the real 4D placement (centroid
of child coords at compose). Stored `trajectory` is a mantissa-packed constituent
manifest (ids/ordinals/RLE), not a path of positions — path metrics
(`laplace_frechet_4d`, Hausdorff) must run on `entity_curve` / `word_curve` (realized
`ST_MakeLine(child.coord ORDER BY ordinal)`). Coordinate equality does not survive
composition; shape lives on the realized curve.

## 7. Read path

The extension ships its SQL function families and native sources
(`extension/laplace_substrate/src/`; live counts in `docs/INVENTORY.md`). The hot
paths are C:

| Source | Entry points |
|---|---|
| `recall.c` | `recall_intent`, `recall`, `recall_session`, `define_fast`, `word_shape_peers_fast` |
| `generate_walk.c` | `walk_branches` (batches natively against `consensus` with per-level capacity derived from frontier × caller breadth), `walk_strongest` (steps via `consensus_walk_edges`) |
| `astar_path.c` | Dijkstra by default; opt-in admissible geometric A* heuristic |
| `prompt_coherence.c` | joint sense/topic/relation election across a prompt's tokens |
| `trajectory_generate.c`, `steered_walk.c` | n-gram descent and topic-steered walk |
| `fold_route.c` | `consensus_upsert`, the server-side fold `ConsensusAccumulatingWriter` dispatches to (`consensus_fold_step.c` backs the `consensus_fold_result` aggregate) |
| `highway_mask.c`, `perfcache.c` | perfcache-backed bit operations over mmap'd blobs |
| `model_factor.c`, `graph_taxonomy/cascade/contrast.c`, `containers_of.c`, `realize_batch.c`, `geometry_successors.c` | model, graph and realization surfaces |

`walk_strongest` ranks by `relation_rank × eff_mu`. `walk_branches` ranks by a fuller
signed weight that additionally uses RD decay, witness saturation and highway-mask
gating; refuted edges carry negative weight. `eff_mu = rating − 2·rd` is the
conservative estimate reads rank by.

`SELECT * FROM ops.api('<substring>')` introspects the installed surface. It MUST be
schema-qualified: the SQL surface lives in nine purpose schemas (`ops`, `consensus`,
`converse`, `lexical`, `taxonomy`, `generation`, `structural`, `chess`, `realize`) that
are deliberately kept off `search_path` (`purpose_schemas.sql.in`), so the bare
`api(...)` form fails on the current layout (#862/#957).

Two mmap'd perfcache blobs are required at runtime — `laplace_t0_perfcache.bin` via
the `laplace_substrate.perfcache_path` GUC and `laplace_highway_perfcache.bin` via
`laplace_substrate.highway_perfcache_path` (`extension/laplace_substrate/src/perfcache.c`).

## 8. Model lane

`engine/synthesis/` reads checkpoints: `safetensors_parser.h`, `sentencepiece_parser.h`,
`bf16_decoder.h`, `tensor_dtype_codec.h`, `f32_gather.h`, `tensor_decompose.h`,
`qk_project_cached.h`, `qk_pairs_threshold[_pruned].h`, `feature_extractor.h`.

`engine/dynamics/` holds the math: `eigenmaps.cpp` (normalized-Laplacian eigenmap of the
consensus graph), `procrustes.cpp`, `gram_schmidt.cpp`, `bilinear_edges.cpp`,
`ffn_edges.cpp`, `model_math.cpp`, `tbb_parallel.cpp`.

Export writes GGUF closed-form: `gguf_writer.h`, `format_writer.cpp`, `arch_template.h`,
`recipe.h`; driven from `app/Laplace.Cli/FoundryCommands.cs` and `FoundryExport.cs`.

A checkpoint enters as a witness like any other source — its tensor cells become rated,
provenanced attestations under governed relation types. It is not stored as weights and
is not reproduced.

## 9. Build

Two toolchains, not interchangeable.

**Linux.** `sudo bash scripts/setup-host.sh` once (runner, PostgreSQL, nginx,
chess-lab, migrations). Thereafter `scripts/pipeline.sh`, which
`.github/workflows/laplace.yml` invokes. Build/install/test/regress are change-aware via
content fingerprints in `build/.stamps/` (`scripts/lib/fp.sh`), and
`scripts/affected-app.py` restricts dotnet work to the affected ProjectReference closure.
Bypass with `pipeline.sh --force-all`. Vendored deps build through
`scripts/build-system-deps.sh`.

**Windows.** `scripts/win/*.cmd`; `env.cmd` is the toolchain source of truth. Invoke
through Bash (`cmd //c "scripts\\win\\test-all.cmd"`), not PowerShell.

| Task | Entry point |
|---|---|
| Rebuild modules | `rebuild-all.cmd` |
| Engine / extension | `build-engine.cmd`, `build-extensions.cmd`, `install-extensions.cmd` |
| Full gate | `test-all.cmd` |
| dotnet / ctest / pg_regress | `test-app.cmd`, `test-engine.cmd`, `regress.cmd` |
| Seed | `db-reset.cmd`, `seed-foundation.cmd`, `seed-step.cmd <source>` |
| CLI | `cli.cmd` |
| Publish to IIS | `publish-deploy.cmd` |
| Regenerate inventory | `docs-inventory.cmd` (`--check` in CI) |

Two build facts that cause silent failures if ignored:

- The extension links the engine **statically**. Engine freshness is not extension
  freshness — after any engine rebuild, run `build-extensions` *and*
  `install-extensions`. Extension SQL changes additionally need
  `build-extensions.cmd --reconfigure` (the version hash is computed at configure time).
- `pg_regress` tests the **installed** extension, not an edited `.sql.in`.

CI: `.github/workflows/laplace.yml` is the build/deploy/test pipeline; `seed-*.yml`
(`foundation`, `knowledge`, `documents`, `code`, `models`, `chess`) drive seeding, with
`_ingest.yml` as the shared callee.

## 10. Runtime

PostgreSQL cluster lives at `/opt/laplace`. Connect with
`psql -h localhost -U postgres -d laplace`, then `SET search_path = laplace, public;`.

Deployables: `Laplace.Cli`, `Laplace.Endpoints.OpenAICompat`, `Laplace.Endpoints.Mcp`
(stdio MCP server), `Laplace.Chess.Uci`, `Laplace.Migrations`. `web/` is the Vite/React
SPA.
