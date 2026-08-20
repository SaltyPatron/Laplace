# SQL cohesion, planner, surface, and reseed audit — 2026-08-18

## Executive verdict

The SQL has received real local optimization work, but it is not yet an
industrial-grade operation substrate. The primary problem is not that the
installed extension script is large. The problem is that semantic operations,
scalar/batch behavior, planner contracts, endpoint exposure, and ingest-derived
state are still governed in different places.

The measured condition is:

- Local read-path work has produced substantial wins: `resolve_name` context
  lookup fell from about 36 seconds to effectively zero, `separator_ids` from
  9.45 seconds to about 15 milliseconds, contextual senses from 7.48 seconds to
  277 milliseconds, and salient facts from 5.17 seconds to 376 milliseconds.
- The SQL corpus nevertheless grew by a net 3,656 lines from `66b176ff` while
  167 SQL files changed. That is compatible with useful local fixes and an
  architecture that is still accumulating implementations.
- Several scalar/batch pairs have independent semantic bodies. One batch is an
  explicit loop over the scalar operation. These are drift surfaces, not merely
  style problems.
- The public operation catalog discovers almost every function in selected
  schemas. It does not declare cardinality, public/internal status, cost class,
  bounds, truncation semantics, safety, version, or receipt behavior.
- Static and AST-assisted analysis finds a large review queue for result caps,
  materialization fences, unknown SRF estimates, and predicates that apply
  functions to columns. Live plans prove a smaller, high-impact set of actual
  pruning and scan failures.
- A recent successful-source serial seed total is 10.41 hours, before choosing a
  representative long Chess/UD history. A two-hour target therefore needs more
  than concurrency: it needs staging, global set consolidation, derived-state
  deferral, and a load/serve index split.
- The native content hash already makes image/audio `255` the same identity, but
  the schema can retain only one singular tier/type claim and the media path
  drops binding reconstruction evidence. A modality mask helps routing, but the
  P0 fix is to split content identity, recipe-specific structure, occurrence,
  and typed testimony.

The greenfield goal should be one operation registry, one canonical
implementation per semantic fact, thin scalar/batch/surface adapters, explicit
planner and cardinality contracts, a canonical append-only modality/recipe
registry with derived masks, and a separately budgeted bulk population pipeline.
That is already the intent of
[`37_Substrate_Operation_ISA.md`](specs/37_Substrate_Operation_ISA.md); the
implementation needs to be made to obey it.

## Evidence and denominators

The repository-wide auditor currently sees:

| Measure | Count |
|---|---:|
| SQL files | 549 |
| SQL lines | 25,861 |
| Auditable SQL units, including bodies and embedded SQL | 2,712 |
| Production SQL files | 491 |
| Production SQL lines | 19,382 |
| Production auditable units | 1,983 |
| Exact clone clusters | 12 |
| Near-clone clusters in the full run | 14 |
| Static findings, all roles | 602 |

The static scanner is intentionally lexical. A separate one-off PostgreSQL AST
pass parsed 508 of 864 production query/body units. The 356 failures are mainly
PL/pgSQL fragments, build placeholders, and embedded named-parameter syntax, not
evidence that those statements are valid or invalid. All AST percentages below
name their denominator; none are presented as coverage of all SQL.

Live catalog, plan, `pg_stat_statements`, and relation statistics were captured
from the seeded PostgreSQL 18.3 database on 2026-08-18. Lifetime table/index
counters include ingest, maintenance, `ANALYZE`, and serving traffic, so they
show physical pressure but do not by themselves attribute it to an API call.

## Repeated implementations and scalar/batch behavior

### What “canonical batch” should mean

A canonical implementation should normally accept a relation internally and
preserve input ordinal. Arrays are a convenient boundary representation; they
are not the relational core. Forcing a caller that already has rows to construct
an array can add materialization and erase useful planner knowledge.

The preferred shapes are:

- SQL: one relational core, with thin scalar and array/table adapters.
- Native code: scalar and batch entry points call the same internal C core; the
  scalar path need not materialize a one-element array.
- Batch result: return `(ordinal, key, result...)` when order or duplicate inputs
  matter. Do not rely on physical output order.
- Parity: scalar, batch, native, and reference paths must agree on identity,
  ordering, null/unknown behavior, scores, and bounds.

### Inventory

| Pair or family | Current shape | Finding | Required direction |
|---|---|---|---|
| `converse.attested_language` / `_batch` | Repaired | One set argmax; scalar is a one-id adapter; nonempty/NULL/empty parity fixture | Seeded plan/buffer acceptance |
| `taxonomy.bubble_up` / `_batch` | Repaired | One set election core; scalar is an ordinal-preserving one-term adapter; explicit bounds only | Seeded plan/buffer acceptance |
| `structural.cluster` / `_batch` | One ordinal-preserving SQL relation core; scalar is a one-seed adapter | Repaired after audit; duplicate seeds share anchor/KNN/curve/render/recurrence work | Keep the non-empty parity fixture and production plan budgets green |
| `realize.realize` / `batch` | SQL ladder versus native C batch | Distinct implementations, but a parity test exists | Retain only behind enforced parity and one declared contract |
| `realize.label`, `render`, `render_text` families | Separate scalar/batch SQL or C entry points | Potential native/reference drift | Share internal core and expand parity coverage |
| `lexical.type_label` / `_batch` | Separate adapters over related render behavior | Duplication is smaller but still contractual | Make core and order guarantees explicit |
| `lexical.word_case_variants` / `_batch` | Separate C entry points with duplicated body regions | Native duplication | Factor a common C implementation |
| `structural.geometry_successors` / `_batch` | Separate native entry points/prepared queries | Same family, different execution paths | Shared native core or explicit non-equivalence |
| `realize.resolve_name` / `_batch` | Scalar supports context; batch does not | These are not the same contract | Add equivalent context batching or give them distinct operation names |
| `converse.compose` / `generation.compose_batch` | Scalar delegates to batch | Good adapter pattern, subject to one-element parity and plan checks | Keep pattern |
| `converse.walk` / `generation.walk_batch` | Scalar delegates to batch | Good adapter pattern, subject to the same checks | Keep pattern |

Overloading scalar and array signatures under one SQL name is not currently a
surface solution. `InstalledOpInvoker` resolves overloads by supplied parameter
names and required arguments, not JSON value type. Scalar/array overloads with
the same parameter name are ambiguous. Fix dispatcher type resolution or use
unambiguous public operation names; do not pretend SQL overloading alone repairs
the remote contract.

## Result-set reduction and top-k

### Quantified review queue

The production static pass finds:

| Pattern | Production units |
|---|---:|
| Function interface with a numeric cap default | 103 |
| Statement unit containing `AS MATERIALIZED` | 81 |
| `LIMIT` without visible `ORDER BY` | 18 |
| Known expensive scalar/fan-out primitive call | 21 |
| `UNION` rather than `UNION ALL` | 20 |
| `count(*)` used as an existence test | 7 |
| `ORDER BY random()` | 2 |

The AST pass found 131 `LIMIT`-bearing `SELECT` nodes in parseable production
units:

- 57 apply the limit after multiple sources, a join, an SRF, or a subquery;
- 47 have no `WHERE` at that `SELECT` level;
- 27 have no `ORDER BY` at that `SELECT` level; and
- 38 units use `row_number`, `rank`, or `dense_rank`.

These are not 57 proven defects. A bounded join can be correct, and a subquery
may already have reduced the relation. They are precisely the nodes whose plans
and result contracts must prove that candidate reduction precedes expensive
rendering, scoring, fan-out, or sorting.

`LIMIT` is a response bound only when it is attached to the operation that owns
the bounded contract. The generic operation invoker currently emits:

```sql
SELECT * FROM operation(...) LIMIT row_cap + 1
```

That detects response truncation but does not guarantee that a materialized SRF
or internal query performed bounded work. It is an envelope, not a work budget.

### Required limit taxonomy

Every limit should be classified as one of:

1. **Semantic top-k** — deterministic total ordering, declared tie behavior, and
   an index or already-bounded candidate set supporting the ranking.
2. **Candidate work budget** — explicitly allowed to abstain or return an
   underfilled result; the receipt reports examined candidates and why it
   stopped.
3. **Transport row cap** — applied after the semantic operation and reported as
   truncation; it never claims the operation itself was bounded.
4. **Explicit arbitrary sample** — rare, named as arbitrary, never accidentally
   produced by a missing `ORDER BY`.

If rendering or policy rejects candidates after ranking, “take k and hope” is
not top-k. Use a declared oversampling/work budget and expose underfill or
abstention.

`ops.surface_sample` is the first repaired example from this queue. It formerly
ranked an invented `max(limit×200, 4000)` attestation prefix and only afterward
discarded subjects outside the requested source×tier. The set query now joins
source-owned tier identity before aggregation, orders once, and applies exactly
the caller limit. It cannot underfill because unrelated rows consumed a guessed
candidate pool, and `limit=0` means zero rather than being promoted to one.

Angular KNN had the same defect at the metric boundary. Raw PointZM chord order
mixed direction with centroid radius, then several readers guessed `k×20/200` or
`k×6/60` prefixes before re-ranking by angle. `laplace_direction_4d` now projects
non-zero coordinates onto S3 and a partial type-1 GiST indexes that expression.
Unit chord is monotone with angle, so angular readers consume the exact caller
bound directly. Shape readers retain raw-coordinate candidate discovery but now
admit exactly their declared candidate/output budget instead of hidden 200/500
floors; that contract does not claim global Frechet completeness.

Global relation ranking no longer treats a raw-eff_mu prefix as though it were a
proof of salience-weighted top-k. Because relation rank is positive and constant
inside a relation type, `consensus.top_relations` can take the exact caller-sized
head from every type's eff_mu index and merge those sufficient heads by edge
rank. The API's band-fact read uses the analogous proof one level up: exact
caller-sized heads per requested band, then the declared band/eff_mu ordering.
The former `max(5000,limit×25)` and `max(limit×4,40)` guesses are gone.

Evidence and salient-fact reads no longer compensate for nullable rendering by
guessing `limit×3+8` candidate pools. Ranking now selects the exact caller-sized
head, one batched render labels that head, and a deterministic hash label makes
rendering total when no lexical realization exists. No winner is silently
dropped, and inspection, explore-web, journal, and index-report limits preserve
the caller's zero instead of silently promoting it to one.

`structural.locale` no longer reports radius populations from a fixed nearest-
3,000 sample. It now uses the unit-direction GiST for an exact nearest neighbour
and an n-D chord bounding box plus exact angular filter for the requested radius
counts. Foundry attribute reads likewise honor the declared degree cap directly;
they no longer raise every cap below 16 or replace non-positive caps with 64.

Vocabulary builders no longer assume lexical filters will accept one out of a
fixed `2×`, `3×`, or `4×` candidate prefix. They rank the complete admitted
population, render in set-based pages sized by the requested output cardinality,
filter, and then take the exact head. `generation.foundry_vocab` also has no
implicit 400,000-trajectory cutoff: `p_trajs` is an explicit optional work budget,
and NULL means the complete admitted corpus.

PostgreSQL documents that `LIMIT` can alter plan choice but does not make an
unordered subset deterministic. A matching B-tree can satisfy `ORDER BY ...
LIMIT` without sorting the whole input; otherwise the sort still consumes its
candidate input. Materialized or multiply referenced CTEs can prevent parent
restrictions from being pushed down, although materialization is useful when an
expensive expression must be evaluated only once. See the official guidance on
[`LIMIT`](https://www.postgresql.org/docs/current/queries-limit.html),
[`ORDER BY` and indexes](https://www.postgresql.org/docs/current/indexes-ordering.html),
and [CTE materialization](https://www.postgresql.org/docs/current/queries-with.html).

## Function calls, indexes, and partition pruning

### How much is suspicious

Of 508 parseable production query/body units, 236 contain predicates. Forty-four
unique units contain a function applied to a column in a predicate: 62
occurrences. This is the useful review denominator, not a claim that all 62
violate indexing.

The most frequent calls were:

| Predicate function | Occurrences |
|---|---:|
| `laplace_trajectory_constituent_ids` | 9 |
| `consensus.eff_mu` | 8 |
| `btrim` | 7 |
| `right` | 4 |
| `word_language` | 3 |
| `is_all_whitespace` | 3 |
| `char_length` | 3 |
| `relation_canonical` | 3 |

Some calls are already supported by expression indexes: nine trajectory calls
match installed trajectory expression indexes, and `eff_mu` can align with the
installed arithmetic expression when inlined identically. Some operate only on
small intermediate results. PostgreSQL can use a function predicate when an
equivalent expression index exists, but expression indexes add write and build
cost. See [Indexes on Expressions](https://www.postgresql.org/docs/current/indexes-expressional.html).

### Proven high-impact failures

The serious class is a function around a partition key before pruning:

- A direct predicate equivalent to `c.type_id = relation_type_id('IS_A')`
  planned eight consensus leaves at root cost 0.17.
- The wrapped predicate
  `consensus.relation_canonical(c.type_id) = 'IS_A'` planned all 216 leaves,
  including 107 sequential scans and 109 index-only scans. An outer `LIMIT 5`
  reduced the reported root cost but did not restore partition pruning.
- `converse.relation_bands()` groups the entire consensus tree through
  `relation_highway_band(c.type_id)`. It has no selective predicate and is a
  public read. Four observed calls consumed 103.57 seconds total and about 1.97
  million blocks read; the nonexecuting plan contains all 216 leaves.
- Contextual lexical senses applies `relation_highway_band(c.type_id)` in three
  consensus branches. Subject predicates can still use leaf indexes, but the
  type partition tree cannot be pruned.
- `generation.attention_centroid` applies
  `relation_highway_bit(c.type_id)` after a subject restriction. It likewise
  forfeits type pruning and should resolve acceptable type ids on the small side
  before the consensus access.

Not every occurrence should be rewritten. In BandFacts, the band function is
applied after an indexed entity-type filter to a small set; it is intentionally
on the reduced side. In `bubble_up`, `relation_canonical` is a postfilter after
direct `type_id = ANY(...)`, so the pruning predicate remains present.

Partition pruning is based on partition constraints, not the presence of an
index. Rewrite the proven cases to resolve type ids/bands once, then filter the
partitioned relation with bare `type_id = ...` or `type_id = ANY(...)`. Verify
`Subplans Removed`, leaf count, buffers, and result parity. See
[PostgreSQL partition pruning](https://www.postgresql.org/docs/current/ddl-partitioning.html).

### Planner contracts are mostly absent

The static pass finds 265 production SRF definitions without a `ROWS` clause.
In the installed catalog, the seven main operation schemas contain 200 SRFs; 192
still have the PostgreSQL default estimate of 1,000 rows and only eight declare a
different value. Unknown cardinality propagates bad join choices through
composed operations.

Do not blanket-set every function to the same new number. Measure cardinality by
operation class, declare `ROWS` and `COST`, and use planner support functions for
input-dependent estimates where justified. PostgreSQL documents the default and
support mechanism in [`CREATE FUNCTION`](https://www.postgresql.org/docs/current/sql-createfunction.html).

## Which SQL scans tables regardless

### Structurally unconditional large-relation scans

Only three production functions were found with an unconditional direct access
to one of the four large logical base relations:

| Function | Relation/work | Classification |
|---|---|---|
| `converse.relation_bands()` | Full consensus aggregation | Public hot-path defect; default should be maintained or approximate, with exact scan opt-in |
| `laplace.highway_mask_rebuild(...)` | Full consensus maintenance pass | Intentional offline maintenance; keep out of serving paths and receipt it as such |
| `laplace.substrate_health(false)` | Exact `count(*)` over all entities | Health cost hazard; use an estimate/default cheap metric and move exact count to deep audit |

`identity_law_violations` is also a deliberate deep corpus audit, but it is
opt-in and not a serving defect.

### Fan-out is not the same as a sequential scan

The AST pass found 206 direct accesses to the four large logical relations:

| Relation | Accesses | With bare partition key | Without bare partition key |
|---|---:|---:|---:|
| `entities` | 46 | 5 tier predicates | 41 |
| `attestations` | 48 | 40 type predicates | 8 |
| `consensus` | 98 | 77 type predicates | 21 |
| `physicalities` | 14 | 2 id predicates | 12 |

“Without partition key” means fan-out across the partition tree, not necessarily
a heap scan. A subject/source predicate may still use an index in every leaf.
That can remain expensive because planning and probing occur hundreds of times.
It also exposes a topology mismatch: many consensus reads are subject-first
while the first partition dimension is type; many physicality reads are by
entity/trajectory while the partition key is id.

Live lifetime statistics make the physical cost visible:

- approximately 254 GB of heap and 313 GB of indexes;
- 264.5 million sequential scan starts and 3.00 billion index scan starts;
- 69.0 billion rows read by sequential scans versus 7.32 billion heap rows
  fetched through indexes;
- 529 tables have been sequentially scanned; 321 read more than ten times their
  live row count through sequential scans.

These counters include bulk population and maintenance. The defensible answer is
therefore: three unconditional large-relation scans are structurally visible;
many more operations fan out, and workload-attributed plan capture is required
to separate serving defects from ingest/maintenance scans.

## Index estate and greenfield load cost

The installed database has about 313 GB of indexes. For indexes at least 1 MB:

| Lifetime-use class | Indexes | Bytes |
|---|---:|---:|
| At least 100 scans | 923 | 190 GB |
| Never scanned | 723 | 68 GB |
| Fewer than 100 scans | 661 | 55 GB |

Thus roughly 123 GB, 39% of index bytes, has weak or zero observed use since
database creation. This is not permission to drop it blindly: the observation
window mixes workloads, rare correctness/operations paths matter, and index
usage statistics can reset. It is enough to reject building every current index
during a greenfield load without a serving-contract benchmark.

Notable expression-index families include:

- `(rating - 2 * rd)`: 732 indexes, about 39 GB, 106,970 scans;
- trajectory constituent ids: 130 indexes, about 18 GB, 842 scans;
- coordinate norm: 65 indexes, about 4.1 GB, zero scans;
- first trajectory constituent: 65 indexes, about 3.1 GB, zero scans; and
- highway-mask bits: 31 GIN indexes, about 365 MB, zero scans.

Create a measured **load index set** and a measured **serve index set**. The load
set contains only keys needed for identity and phase consolidation. Build the
serve set after bulk base/fold phases and promote an index into it only with a
representative plan or operational requirement.

## PostGIS comparison and extension installation

Local installed artifacts provide the useful comparison:

| Installed artifact | Lines | Approx. size | Functions/wrappers |
|---|---:|---:|---:|
| PostGIS 3.6.3 versioned SQL | 43,016 | 7.3 MB | about 754 |
| Laplace substrate versioned SQL | 19,213 | 968 KB | about 439 at artifact inspection time |

`CREATE EXTENSION laplace_substrate` currently completes in roughly 2.5–3.3
seconds. Raw install-script size is not the reseed bottleneck.

The implementation mix is more important:

| Artifact | SQL bodies | C wrappers | PL/pgSQL bodies |
|---|---:|---:|---:|
| Laplace | 318, about 440 KB | 80, about 20 KB | 41, about 146 KB |
| PostGIS | 132, about 38 KB | 593, about 126 KB | 22, about 57 KB |

PostGIS ships versioned extension SQL that registers types, operators, and many
thin C-backed functions. Laplace already does the correct packaging-level work:
modular source fragments generate a versioned install/upgrade artifact. It
should preserve that model while moving repeated orchestration into shared SQL
cores or native cores. It should not combine the seed into `CREATE EXTENSION` or
optimize for a cosmetically small generated SQL file.

PostgreSQL's extension mechanism is transactional and version-script based; the
PostGIS documentation likewise describes extension files plus supporting
binaries and `CREATE EXTENSION`. See
[Packaging Related Objects into an Extension](https://www.postgresql.org/docs/current/extend-extensions.html)
and [PostGIS installation](https://postgis.net/docs/en/postgis_installation.html).

## Native cores and modality perfcaches

### Architectural boundary

The intended PostGIS-like boundary is appropriate for this system:

- SQL declares typed functions, relational access, transaction/snapshot
  boundaries, volatility, cardinality, and planner-visible filters. It may retain
  short set-oriented joins that PostgreSQL can optimize better than an opaque
  native implementation. It should not contain 200–470-line semantic engines.
- C# owns transport, authentication, session/stream lifecycle, operation
  composition, cancellation, and receipt delivery. It should not reimplement
  substrate facts or scalar/batch algorithms.
- C wrappers own PostgreSQL ABI conversion, memory contexts, errors, and one
  operation call. C++ implementation cores sit behind a stable `extern "C"` ABI.
- Native cores own bulk decomposition, identity composition, graph kernels,
  scoring, geometry, fold primitives, and modality transforms.
- SPI supplies set-sized database inputs and persists set-sized outputs. An SPI
  call inside a native per-row loop is still RBAR; it has merely hidden the
  round trips inside the extension.
- Perfcaches supply immutable exact lookups for deterministic finite/hot derived
  structures. They accelerate the canonical operation; they do not define a
  competing result.

```text
MCP / HTTP / OpenAI / typed client
                |
       operation registry + dispatcher
                |
        thin SQL / C# contract adapters
                |
          native operation core
          /         |          \
 prepared bulk   perfcache    SIMD/math
     SPI           bundle      kernels
          \         |          /
        rows + operation receipt
```

PostgreSQL documents SPI as a supported extension interface and states that a
prepared statement can avoid repeated parse analysis and reuse an execution
plan. Saved plans can survive `SPI_finish` for later calls. PostgreSQL also
requires particular care at the C++ boundary: a C interface, matched allocation
discipline, no C++ exceptions crossing into PostgreSQL, and no non-POD objects on
a stack across backend calls that may `longjmp`. See
[`SPI_prepare`](https://www.postgresql.org/docs/current/spi-spi-prepare.html),
[`SPI_execute_plan`](https://www.postgresql.org/docs/current/spi-spi-execute-plan.html),
and [C-language/C++ extension guidance](https://www.postgresql.org/docs/current/xfunc-c.html).

The native sources currently contain 209 references to the principal
prepare/execute SPI APIs: 64 `SPI_prepare`, 57 `SPI_keepplan`, 40
`SPI_execute_plan`, 49 `SPI_execute_with_args`, and one direct `SPI_execute`.
That is evidence of substantial native work already, not proof that all hot
paths are correct. The 50 non-reused execution sites and any execution site
inside a row/candidate loop form a specific review queue. Each should become one
prepared batch plan, a cursor/stream where result volume requires it, or a
documented cold one-shot.

The managed path no longer delegates that decision to Npgsql's arbitrary
`MaxAutoPrepare=50` / two-use LRU. Canonical typed reads explicitly prepare their
fixed SQL, and the ingest presence/merge/upsert/mask loops prepare their set-sized
commands once per physical connection and reuse the command for every chunk. DDL,
dynamic operation dispatch, and genuine one-shot maintenance remain unprepared.

### Content identity law

The non-negotiable cache key law is:

> The same canonical recovered **content** produces the same root hash. Facts,
> interpretations, or objects obtained *from* that content do not participate
> in its content hash.

The path is:

```text
container/codec bytes
        |
canonical recovered content + shape/order
        |
canonical decomposition -> shared T0 leaves -> composed content root
        |
        +---- derived labels/features/objects/embeddings/relations
                         |
               scoped testimony/projections
```

Consequences by modality:

- Text and code with the same canonical source content reach the same content
  root regardless of path/source. AST kinds, symbols, calls, classifications,
  or model analysis are facts from that content and cannot change the root.
- Different image container encodings that recover the same canonical channel
  values and spatial structure reach the same image content root. Detected
  objects, captions, embeddings, and spectral features are testimony.
- Different audio containers that recover the same canonical samples and
  temporal/channel structure reach the same audio content root. Transcripts,
  speakers, onsets, and learned features are testimony. Sample rate and channel
  layout must at least survive as binding reconstruction evidence; whether they
  enter canonical content identity is a declared recipe decision, never an
  accidental omission.
- Video identity follows canonical frame content and temporal order, not codec,
  filename, scene labels, or detected actors.
- Chess positions and moves use their binding content representation; engine
  evaluation, opening name, player, outcome, and policy are facts about them.
- Model/tensor content is keyed by its canonical payload, dtype/shape, and
  binding recipe. Factorization, spectral analysis, eval scores, and generated
  labels are versioned derived projections.

The simplified three-channel pixel makes the boundary concrete:

```text
[[2,5,5], [2,5,5], [2,5,5]]
```

The T0 codepoints `2`, `5`, and `5` compose the content id for `255`. All three
occurrences reuse that id. `255` retains that one identity when it is used as a
channel intensity, an IPv4 octet, the maximum unsigned byte, a sample value, or
something not invented yet. The ordered three-value structure composes the
pixel content id; ordered pixels compose patch, region, image, and scene roots.
Claims such as red/green/blue channel, intensity, octet, sample, `Pixel`,
`Patch`, and `Image` describe occurrences through typed scoped testimony. They
are not hash salt.

### Recursive content, not circular identity

The corrected building-block law is recursive:

```text
T0 codepoint                              floor building block
       |
ordered/unordered canonical composition
       v
content node                              made from building blocks
       |
reused as a constituent of another composition
       v
higher content node                       still the same child identity
```

A composite does not change id when a higher composition uses it as a building
block. “Used as a constituent” is an edge/occurrence role. The only irreducible
alphabet is the T0 codepoint set; all higher units are content composed from
content.

This is recursion over a content-addressed DAG, not a circular reference. A
composition's children must already have identities, and composition edges must
be acyclic. A self-containing “set of all sets” cannot be a content-composition
node because its hash would depend on itself. Self-reference may be represented
as later testimony/reference about an already identified node, but it cannot be
smuggled into the Merkle preimage.

The current `PhysicalityType` names do not enforce this law:

- `BuildingBlock = 2` exists, but an architecture test rejects it in production
  decomposers; even Unicode T0 codepoints are emitted as `Content = 1`.
- nearly every ordered text, grammar, image, audio, code, and chess composition
  is also `Content = 1`;
- `Set = 5` distinguishes unordered trajectory semantics, while `Projection`
  and `ProjectionOutput` distinguish calculated representation classes.

Therefore this column is a **representation/trajectory kind**, not a semantic
entity type and not a modality. In the greenfield schema either make the floor
meaning real (`FloorBuildingBlock` for codepoint physicalities and `Content`
for composed structures), or rename the column/enumeration to
`representation_kind` and keep floor status in the structural contract. A
higher content node remains `Content` when reused as a child; it does not need a
second physicality merely because it is now somebody else's building block.

### Current identity/materialization conflict

The native composer preserves the hash half of this law but the storage path
cannot currently preserve all of the typing evidence:

- `resolve_number_id` gives image `255` and audio sample `255` the same
  `laplace_content_root_id("255")`.
- `emit_node` assigns image tier 1 the single entity type `Number`, but assigns
  audio tier 1 the single entity type `Sample`.
- `laplace.entities` has primary key `(id, tier)` and one non-null `type_id`.
  It cannot store both type claims for the same content id and tier. Arrival
  order therefore determines which modal type occupies the entity row.
- `NpgsqlWorkingSetApply.DistinctEntityRowIndices` deduplicates staged entities
  by `id` alone even though the database key is `(id, tier)`. Run-scoped
  persisted/claimed caches are also id-only. Cross-tier observations of the
  same content can be skipped before the keyed database probe sees them.
- `entities.tier` is documented as a floor and `identity_law_violations()` says
  the same id at more than one tier is a violation, yet the primary key is
  `(id, tier)`. The schema physically permits and the ingest caches inconsistently
  suppress exactly what the law says must be one logical entity.
- `physicalities.type` is `Content` for both media ladders and physicality id is
  only `hash(entity_id, type)`. It has no modality, recipe, or reconstruction
  contract. One content/type id can therefore hold only one trajectory even when
  multiple valid structural interpretations need to coexist.
- the trajectory vertex stores child id, ordinal, run length, five tier bits,
  and optionally a 21-bit codepoint. It stores no entity semantic type, modality,
  recipe, dimensions, sample rate, or channel role.

This is a P0 correctness defect, not a naming preference. The greenfield storage
shape should separate:

1. **content identity** — one immutable content hash, with one logical entity
   row keyed by id;
2. **structural realization** — a deterministic recipe-specific physicality and
   constituent trajectory; its key includes at least content id,
   representation kind, and recipe id if multiple realizations are legal;
3. **structural occurrence/interpretation** — modality, tier/floor, parent/path
   or scope, binding shape/rate/channel contract, and provenance; and
4. **typed testimony** — multi-valued, source/context-scoped claims such as
   number, sample, channel role, pixel, detected object, or semantic class.

If `entities.type_id` remains, it may hold only a single intrinsic structural
class that cannot disagree across entry paths. Modal/semantic roles must be
relations/attestations. Every novelty, presence, claimed, and persisted key must
match the complete storage key; no id-only cache may silently collapse a
distinct tier/occurrence/type claim.

This separation answers the tier ambiguity directly. A text tier-2 occurrence
and an image tier-2 occurrence may point at the same content identity and even
the same generic physicality when their constituent trajectory is identical.
Laplace's generic geometry/trajectory kernels read them the same way. Ingest and
export select a declared interpretation/recipe only when modality-specific
recovery or serialization matters. `tier = 2` alone is never globally meaningful
without that recipe/interpretation context.

### Modality mask: derived routing, not identity

A modality mask is the right companion to the highway mask, provided its
contract is narrower than the authoritative interpretation evidence:

| Layer | Contract |
|---|---|
| Append-only modality registry | Stable bit number, canonical modality id/name, recipe family, aliases, introduced generation; aliases never consume new bits |
| Interpretation rows/testimony | Authoritative exact evidence: `(content, structural realization, modality, recipe, tier/floor, scope, source)` plus binding reconstruction facts |
| Entity modality mask | Rebuildable OR-summary of modalities in which the content has been observed; zero, one, or many bits |
| Physicality modality mask | Optional applicability summary when a recipe-specific realization/projection is valid for only some modalities |
| Native/perfcache form | The same fixed-width bit layout, AND/OR/popcount primitives, manifest generation, app/PostgreSQL parity, and exact fallback discipline as highway routing |

No canonical modality roster currently exists to assign those bits. There are
at least three incompatible namespaces in production source:

- native `laplace_modality_t` / managed `MediaLadderKind` contains only
  `Image = 1` and `Audio = 2` and is explicitly described as a temporary media
  type-floor selector;
- model ingestion has `Modality { Text, Vision, Audio, Diffusion, Unknown }` with
  different semantics and ordinal assignments; and
- grammar composition calls language/format ids such as `python`, `sql`,
  `json`, `pgn`, and `markdown` “modality ids.”

The registry must distinguish a coarse, composable modality family from an
exact recipe/grammar/codec. `image` versus `vision` is one canonical family plus
aliases unless the substrate contract proves otherwise; Python and SQL are code
recipes, not new global modality bits; `Unknown` gets no identity-bearing bit.
A code file can intentionally carry both text and code bits, just as a numeric
content node can accrue text, image, audio, or structured-network observations.

The mask must not enter the content hash or decide a unique type. A trajectory
may be cross-modal; its mask simply contains multiple bits. Generic Laplace
operations can ignore the mask and operate on ids/geometry. Ingest/export and
remote operation dispatch can use it as an indexed prefilter, then resolve the
exact interpretation and recipe. The mask cannot replace channel role,
dimensions, sample rate, tensor dtype/shape, grammar, recipe version, scope, or
provenance.

Do not place the whole modality mask in the 52 mantissa flag bits. The existing
format already multiplexes ordinary content, testimony, and factor layouts:
content uses bits 0--5 and optionally 31--51, testimony discriminates at bit 6
and consumes 7--42, and factor discriminates at bit 7 and consumes 8--42. The
apparently free ordinary-content range is an ABI trap, too small for an
append-only global modality roster, and a mask usually describes the structural
realization rather than each vertex. For child-specific meaning, derive roles
from `(recipe, ordinal)` or store explicit scoped occurrence testimony.

Population must also avoid copying the highway mask's current global advisory
lock. Ingest can set modality bits with partition-local/set-sized OR updates;
deletion/eviction queues exact ids for recomputation, and a full rebuild remains
an offline repair/version-renumbering operation.

### Missing reconstruction evidence in the current media path

The media adapter carries facts that the emitted substrate presently loses:

- `ImageIngestRecord` carries RGBA, width, and height, but `WalkWitness` emits
  only a completion marker and filesystem `FileMetadata`. The native image tree
  uses dimensions to group iteration, but no dimension/shape fact is attested.
  Direct execution during this audit gave the same image root
  `b44598e9d8966de4a7f7da0dfa9cf467` for identical eight RGBA bytes supplied as
  both `1 x 2` and `2 x 1`. The flattened values cannot reconstruct their layout.
- `AudioIngestRecord` carries `SampleRate`, but `AudioTierSpine.BuildTree` and
  `laplace_audio_root_id` accept only PCM samples. `WalkWitness` does not attest
  rate or channel structure. Identical PCM at different rates is therefore
  indistinguishable to content reads and cannot be exported faithfully from the
  substrate alone.
- image `Channel` nodes and single-sample audio wrappers are eligible for
  single-child collapse. This is compatible with same-content identity only if
  the discarded label/role remains recoverable from the parent recipe and
  ordinal or from explicit testimony. Today no such media interpretation record
  is emitted.

Greenfield acceptance needs round-trip fixtures, not just deterministic hashes:
decode two containers to the same canonical recovered content, ingest in both
orders, reconstruct with an explicit recipe, and compare exact values, order,
shape/rate/channel contract, modality/type testimony, and root id.

This is already the intent of
[`modality-ladder-law.md`](invention/modality-ladder-law.md): all modality
composition bottoms out at the shared Unicode codepoint floor. Only codepoints
form the T0 alphabet. The prior error was treating packed RGBA tuples, PCM
values, or other recovered structures as atomic T0 identities. The native cache
design must make the correct tier law executable across every entry path.

Tier 1 is the first binding, modality-specific logical n-gram composed from T0
codepoints:

| Modality | Shared floor used | First logical n-gram |
|---|---|---|
| Text | Unicode codepoints | Grapheme |
| Image | Decimal digit codepoints | Canonical number for one recovered channel value |
| Audio | Sign/digit codepoints | Canonical sample number |
| Chess | Codepoints of the binding surface | First chess unit/token defined by the chess recipe |
| Code | Codepoints of canonical source content | First binding source/grammar unit; AST facts remain above content identity |

Higher levels are compositions of those n-grams. A one-codepoint n-gram may
naturally collapse to its T0 identity; that does not authorize a separate atom
space. “Alphabet,” “tier,” and “cache layer” therefore must not be used as
interchangeable terms.

### T0 and the cache dependency graph

T0 should remain named as the universal identity floor in public architecture.
Its implementation happens to be the anchor of the cache dependency graph:
every above-floor identity cache declares the exact T0 generation it was built
from, and no modality mints an alternate T0.

The installed host currently contains:

| Blob | Bytes | Current runtime state |
|---|---:|---|
| T0 codepoint/UCD/UCA | 89,580,004 | App and PostgreSQL load/prewarm; universal floor |
| Highway relation law | 10,141 | App and PostgreSQL load/prewarm; shared routing metadata |
| Chess position compose floor | 752,544 | App and PostgreSQL load/prewarm |
| Chess transition floor | 251,920 | Managed mmap loader; file present, but primary build/deploy gate does not declare or wire it like the other required blobs |
| Modality number compose floor | 20,624 | Built and installed; native loader/tests exist, but no production app interop or PostgreSQL GUC/prewarm/load path calls it |

This is a delivery-shape defect. Adding the next modality by copying another
GUC, loader, prewarm branch, environment variable, app wrapper, install rule,
and pipeline check guarantees more partial landings.

Replace the per-blob wiring with a **versioned cache bundle manifest** and one
shared loader registry. Each cache descriptor declares:

| Descriptor field | Contract |
|---|---|
| Cache id and format version | Stable identity independent of filename |
| Semantic class | content floor, finite vocabulary, transition, point, trajectory, factor/projection |
| Dependency hashes | T0 generation plus recipe/relation/source-manifest generations used to build it |
| Key/value schema | Exact typed meaning; content keys remain distinct from derived-projection keys |
| Writer and verifier operation | Canonical rebuild and parity path from PostgreSQL/native truth |
| Required/optional consumers | PostgreSQL, ingest app, serving app, modality/operation ids |
| Load policy | required, optional fallback, prewarm, or offline only |
| Compatibility | magic, endianness, layout, bounds, checksum, minimum operation version |
| Runtime metrics | generation, bytes, records, hits, misses, fallback, corrupt/stale rejects, load time |

Publish a complete bundle into a generation directory, verify all dependencies,
then atomically publish one manifest/current-generation pointer. Pin one bundle
generation for the postmaster/process lifetime so backends cannot observe mixed
T0/modality generations. Cache miss means execute the canonical core or return a
declared unavailable result; it can never mean fabricate a nearby answer.

### Modality cache layers

Perfcaches should be shared by semantic class, not created reflexively per
endpoint or file format:

| Layer | Shared core/cache opportunity |
|---|---|
| Universal floor | Codepoint id, coordinate, Hilbert, segmentation properties, decomposition/composition, reverse id lookup |
| Numeric compose floor | Canonical decimal number roots shared by image, audio, model, code, chess, and metrics; extend signed/range scope by a new version rather than private modality ids |
| Relation/routing law | Canonical relation id/bit/band/rank and mask operations; shared by all modalities |
| Finite above-floor compositions | Chess piece-square/position units, format grammars, AST kinds, media tier kinds—only where the finite set and recipe are binding |
| Transition/trajectory | Exact `(state, operation) -> state` or constituent-path lookup for deterministic hot corpora; source manifest and recipe are part of the cache generation |
| Geometry/point | Exact id-to-point/Hilbert records and bounded neighbor auxiliaries; point facts only |
| Calculated projections | Model factors, spectral bases, embeddings, FFT/features keyed by `(content_root, operation_version, scope)`; evictable testimony accelerators, never content identity |

Do not attempt an exhaustive cache of all RGBA pixels, audio windows, video
frames, or arbitrary ASTs. Cache the finite shared number/grammar floors and the
observed hot deterministic compositions for a declared source generation.
Content-addressability already supplies dedupe; the bundle should exploit it,
not build a second identity system.

### SIMD and numerical-library placement

The host used for this audit is an Intel i7-6850K with AVX2, FMA, BMI2, and
POPCNT, but no AVX-512 or VNNI. Therefore AVX2 is the real deploy ceiling on this
machine. VNNI/AVX-512 kernels may be valuable on later hardware, but the binary
must retain a correct scalar/AVX2 path and choose kernels by runtime CPU feature
dispatch. Compiling the only path for a newer ISA would make portability and
recovery worse, not faster.

Use each tool where its data shape matches:

- AVX2/AVX-512: contiguous coordinate distances, centroids, score transforms,
  bit masks/popcounts, decode/gather kernels, and batched fixed-width records.
- VNNI: quantized integer dot-product/scoring kernels when the contract really
  uses compatible integer accumulation. It is not a double-precision geometry
  accelerator.
- Eigen: small/fixed or dense linear algebra with aligned contiguous storage;
  allow its packet dispatch instead of hand-writing every vector expression.
- oneMKL: BLAS/LAPACK, FFT, and vector-math workloads large enough to amortize
  setup. Keep PostgreSQL backend thread count controlled to avoid multiplying
  PostgreSQL concurrency by nested MKL/TBB concurrency.
- Spectra: the existing large sparse partial-eigenproblem class. It is designed
  to obtain a small `k` from a much larger sparse matrix; it is not a generic
  graph top-k implementation.

Intel documents that VML operates on vectors in memory and that ISA support
varies by processor; Eigen enables supported SIMD automatically and has
alignment requirements for fixed-size vectorizable objects; Spectra operates
through matrix-vector products for large sparse eigenproblems. See the
[Intel Intrinsics Guide](https://www.intel.com/content/www/us/en/docs/intrinsics-guide/index.html),
[oneMKL vector functions](https://www.intel.com/content/www/us/en/docs/onemkl/developer-reference-c/2024-2/vector-mathematical-functions.html),
[Eigen vectorization guidance](https://libeigen.gitlab.io/pages/faq/), and
[Spectra's primary repository](https://github.com/yixuan/spectra).

Every optimized kernel needs a scalar/reference implementation, randomized and
edge-case parity, deterministic ordering/tie checks, ISA-dispatch tests, and a
representative benchmark that records cycles/element, bytes/element, allocation,
and end-to-end buffers. A faster inner loop does not compensate for scanning 216
partitions or fetching millions of unwanted tuples through SPI.

## SQL/API/OpenAI-compatible/MCP cohesion

### Current surface

The live `ops.api()` result contains 463 entries and 447 distinct names: 456
function entries, five procedures, and two aggregates. It contains 115 array
signatures across 107 names and 31 entries across 15 overloaded names.

The catalog exposes only `name`, `args`, `returns`, and `kind`. Schema membership
is effectively the publication policy. Consequences:

- internal helpers and expensive diagnostics are remotely discoverable beside
  stable public operations;
- an aggregate is catalogued, but `InstalledOpInvoker` distinguishes only
  procedure versus non-procedure, so it tries to invoke an aggregate as a table
  function;
- the outer row cap can truncate output but cannot bound internal work;
- overload resolution cannot distinguish scalar from array by JSON type; and
- no surface can discover cardinality, bounds, cost class, safety, receipt, or
  deprecation behavior.

There are 24 hand-written MCP tools and 98 OpenAI-compatible HTTP routes. Source
mapping finds 96 direct references to 75 distinct `NpgsqlSubstrateReads` methods
across those surface projects. The generic MCP `op` and HTTP `/v1/op` share
`InstalledOpInvoker`; most typed routes remain hand-mapped. The OpenAI chat path
correctly needs orchestration for sessions, streaming, and billing, but its
semantic database work should still execute declared canonical operations and
produce the same operation receipt as an equivalent MCP request.

### Required operation registry

Replace schema-wide `pg_proc` discovery as the remote contract with an explicit,
installed registry. At minimum each published operation declares:

| Field | Purpose |
|---|---|
| Stable operation id and version | Contract identity independent of SQL helper name |
| Implementation signature | Exact SQL/native entry point and declared argument types |
| Cardinality | one, optional-one, many, aggregate, stream, or procedure |
| Adapter shapes | scalar, relation, array, cursor/stream |
| Publication | internal, SQL, MCP, HTTP, OpenAI semantic program |
| Safety | read, write, destructive, maintenance; authentication policy |
| Cost/work class | interactive, bounded, deep audit, offline maintenance |
| Ordering and bounds | total order, candidate budget, response cap, truncation/underfill semantics |
| Planner contract | volatility, parallel safety, `ROWS`, `COST`, support function |
| Receipt schema | input count, candidates examined, rows returned, truncation, elapsed, plan/IO summary where enabled |
| Lifecycle | owner, parity test, introduced/deprecated version |

Generate or validate MCP/OpenAPI/HTTP adapters from this registry. Typed product
endpoints can remain typed, but they must call the same dispatcher/program as the
generic operation surface. An endpoint-specific implementation of the semantic
fact is a gate failure.

## Reseed design for a two-hour target

### Measured starting point

The most recent successful run per 19 sources sums to 37,494 seconds, or 10.41
hours, if serialized. Largest recent successes include:

| Source | Duration | Units |
|---|---:|---:|
| Wiktionary | 4.00 h | 10.48 M |
| Tatoeba | 3.35 h | 40.89 M |
| ConceptNet | 1.93 h | 24.33 M |
| OMW | 18.1 min | — |
| FrameNet | 12.6 min | — |
| WordNet | 7.4 min | — |

The latest Chess entry is much shorter than an earlier successful 3.24-hour run,
and UD has long interrupted runs. Therefore 10.41 hours is not a safe definition
of a representative full seed. It is a lower-bound campaign baseline until the
canonical source manifest and versions are frozen.

A two-hour target needs at least a 5.2x improvement over that lower bound, and
more than 6x against a representative 12-hour campaign. The current healthy
`COPY` path has demonstrated roughly 106,400 rows/second; the measured dominant
cost is consensus fold/derived work rather than raw COPY. The existing detailed
evidence is in
[`INGEST_THROUGHPUT_FINDINGS_2026-08-10.md`](archive/reports/INGEST_THROUGHPUT_FINDINGS_2026-08-10.md).

### Greenfield phase model

1. **Install schema and native operations** — versioned extension artifact only;
   seconds, no seed data.
2. **Parse and bulk-stage all sources** — binary `COPY` into lean
   staging/unlogged relations where crash semantics permit; record source/file
   manifests and deterministic hashes.
3. **Global identity/dedupe merge** — consolidate once per phase/partition, not
   once per source or per unit. Preserve collision/identity-law failures rather
   than hiding them with `DO NOTHING`.
4. **Global attestation merge** — set-oriented, partition-aware, with only the
   load index set present.
5. **Consensus fold once** — fold across all staged testimony by disjoint
   partitions. Remove or partition the global highway-mask advisory-lock convoy
   only after lock-wait sampling proves the exact replacement.
6. **Derived projections once** — highway/modality masks, catalogs, statistics,
   and other derived state are post-base phases, not repeated inside each source
   rung.
7. **Build the serve index set** — parallel within measured I/O/WAL limits;
   validate constraints in bulk.
8. **Analyze, validate, and publish** — collect statistics after load, run exact
   law checks once, atomically mark the content-addressed seed manifest ready.

PostgreSQL explicitly recommends `COPY`, creating indexes after bulk population,
bulk foreign-key validation, appropriate `maintenance_work_mem`/WAL sizing, and
`ANALYZE` afterward. See [Populating a Database](https://www.postgresql.org/docs/current/populate.html).

### Admission budget, not a promise

The first full greenfield benchmark should test this 120-minute envelope:

| Phase | Admission ceiling |
|---|---:|
| Parse and stage | 35 min |
| Base identity/attestation consolidation | 20 min |
| Consensus fold | 35 min |
| Required serving indexes | 20 min |
| Analyze and validation | 10 min |

These ceilings are hypotheses to falsify on the target hardware. In particular,
building the proven serve subset from a current 313 GB index estate may exceed 20
minutes. If so, the answer is to shrink/partition the required serve set or
change the product readiness boundary, not silently declare the seed complete
before required indexes exist.

Each phase must be resumable from a content-addressed checkpoint. Report wall
time, CPU, bytes read/written, WAL, temp bytes, lock waits, rows in/out, duplicate
reduction, and per-partition skew. `scripts/ingest-baselines.json` currently omits
the largest sources and its 500k rows/second floor is printed rather than
enforced; it cannot gate this SLO as written.

## Prioritized program

### P0 — stop semantic and operational drift

1. Install an explicit operation registry and make it the only publication
   allow-list. Exclude aggregates/internal/deep/offline functions by default.
2. Make MCP `op`, HTTP `/v1/op`, typed endpoints, and OpenAI semantic programs
   execute the same operation dispatcher and receipt model.
3. Establish one native operation ABI and a thin-wrapper gate. SQL/C# declares
   and composes the operation; shared native scalar/batch cores own algorithmic
   work, and SPI exchanges sets rather than rows.
4. Install the versioned perfcache bundle registry. Make T0 the required
   dependency of every above-floor identity cache, wire modality-number and
   chess-transition through the same build/install/load/status path, include the
   append-only modality-bit registry, and expose generation/hit/miss/fallback
   metrics.
5. Repair content materialization before media seed: split immutable content
   identity from recipe-specific structural realizations, modality occurrences,
   and multi-valued typed testimony; make every ingest
   dedupe/presence/run-cache key match the actual storage key. Add the derived
   modality-mask projection without placing it in hashes or mantissa vertices.
   Prove text/image/audio entry-order independence with shared numeric content
   such as `255` and exact media reconstruction of shape/rate/channel evidence.
6. Fix the proven scan/pruning hazards:
   - make relation-band counts maintained/estimated by default and exact opt-in;
   - remove exact entity count from default health;
   - pre-resolve type ids for contextual senses and attention centroid;
   - verify the newly native tier batch-existence probe against the installed
     slow query before declaring the live defect fixed.
7. Add representative plan and IO gates. A performance claim is not accepted
   from elapsed time alone: record leaf count, pruning, buffers, rows, temp, WAL,
   settings, and result fingerprint.

### P1 — canonicalize execution shapes

1. Extract shared cores for attested language and bubble-up.
2. Keep the set-based `structural.cluster_batch` regression and bounded
   production plan receipts green; it replaced the audited `FOREACH` RBAR path.
3. Factor paired C entry points through shared native cores, then move the
   remaining large hot SQL semantic engines behind those cores in measured
   order. Preserve short planner-visible set joins when they win.
4. Add scalar/batch/native parity fixtures for every published pair, including
   duplicates, nulls, empty input, ordering ties, and over-budget behavior.
5. Classify all numeric defaults and `LIMIT`s using the four limit categories;
   sweep the 81 materialization fences with plan evidence.
6. Declare measured `ROWS`/`COST` and correct volatility/parallel safety for the
   published operation graph. `track_functions` is currently off; enable the
   appropriate measurement window while remembering that inlined SQL functions
   are not tracked. See [runtime statistics configuration](https://www.postgresql.org/docs/current/runtime-config-statistics.html)
   and [function volatility](https://www.postgresql.org/docs/current/xfunc-volatility.html).

### P1 — prove the reseed architecture

1. Freeze a canonical full-seed manifest and establish one uninterrupted
   baseline.
2. Benchmark stage → base → fold → derive → index → analyze as separate phases.
3. Defer the weak/unproven serving indexes and measure their later builds.
4. Consolidate across sources before folds/derived masks; avoid repeating global
   work per source.
5. Capture lock waits during the expensive CILI/WordNet folds before changing
   the global mask lock.
6. Enforce phase and total budgets in CI/operator workflows, including the large
   sources absent from the current baseline file.

### P2 — revisit physical topology with the operation matrix

Build a matrix of published operations by equality/range keys, order, expected
cardinality, and frequency. Use it to decide whether consensus needs an
additional subject-oriented lookup, fewer first-level partitions, or a different
partition hierarchy, and whether physicality access needs a trajectory/entity
lookup relation. Do not repartition from a single slow query or from lifetime
sequential-scan counts alone.

## Acceptance gates

The cleanup is complete only when all of these are true:

- every remotely reachable semantic operation is an explicit registry entry;
- every registry entry has declared cardinality, ordering, bounds, safety, cost,
  planner metadata, receipt fields, and a parity/contract test;
- SQL and C# operation implementations are thin contract/orchestration adapters;
  algorithmic scalar and batch work terminates in the same native core;
- scalar and batch adapters have one semantic core, or an explicit documented
  reason they are different operations;
- no production batch loops over a scalar database operation;
- cross-codec/cross-entry fixtures prove that the same canonical recovered
  content has the same root hash, while labels, embeddings, detections, AST
  facts, engine evaluations, and other derived testimony cannot affect it;
- text, image, audio, code, chess, and model entry order cannot change or drop
  tier/occurrence/type testimony for a shared content id; ingest dedupe keys are
  identical to their database uniqueness keys;
- codepoints are the only floor building blocks; every higher content node is a
  canonical acyclic composition of existing content ids and can be reused as a
  constituent without changing identity;
- a modality mask is reproducible from authoritative interpretation evidence,
  supports zero/one/many and cross-modal content, never affects content hashes,
  and has native/app/PostgreSQL bit-layout parity;
- media round trips preserve exact recovered values, ordering,
  dimensions/sample rate/channel layout and recipe while container/file facts
  remain scoped testimony;
- every perfcache is published in one verified bundle generation, declares its
  T0/recipe/source dependencies, and has exact canonical fallback/parity;
- default health and discovery endpoints perform no exact full-corpus scan;
- representative plans enforce partition-leaf and buffer budgets for published
  interactive operations;
- top-k results have a total order and a measured reduction path, while work and
  transport caps expose truncation/underfill;
- caller-supplied limits pass unchanged through adapters: the chess reads no
  longer promote zero to one or truncate requests at 200, installed operations
  no longer truncate every explicit request above 2,000, and
  `generation.consensus_peer` has no fixed 48-row candidate pool behind `p_k`;
- a canonical full seed is content-addressed, resumable by phase, and completes
  inside the admitted hardware budget with required serving indexes and exact
  validation included; and
- generated extension install/update artifacts are reproducible and fast, while
  seed population remains a separate operation.

## Things not to do

- Do not delete every function predicate or materialized CTE from a static
  finding. Measure the plan and preserve intentional small-side computation.
- Do not replace every scalar entry point with a one-element array call. Share
  the core without forcing materialization.
- Do not remove semantic bounds wholesale. Name their contract and expose
  underfill/truncation.
- Do not drop zero-scan indexes solely from lifetime counters. Re-run a declared
  workload and operational-recovery suite first.
- Do not put seed data or multi-hour derivation into `CREATE EXTENSION` to imitate
  PostGIS packaging.
- Do not declare a reseed win from a subset/latest-only source ledger.
- Do not accept a local timing improvement without result parity, plan shape,
  buffers, and representative cardinality.
- Do not make modality, tier, a singular contextual type, or a modality mask
  part of a content hash. Do not use the mask as a substitute for exact recipe,
  role, shape/rate, scope, or provenance evidence.

## Reproduction

Run the dependency-free corpus audit and its tests:

```bash
python3 scripts/sql-audit.py \
  --json build/sql-audit/findings.json \
  --markdown build/sql-audit/report.md
python3 scripts/test-sql-audit.py
```

Static evidence is a queue for measurement. Plan remediation should use
`EXPLAIN (ANALYZE, BUFFERS, WAL, SETTINGS, FORMAT JSON)` on representative data,
plus a result fingerprint and cold/warm distinction. PostgreSQL's
[`EXPLAIN` documentation](https://www.postgresql.org/docs/current/using-explain.html)
defines what the plan nodes and machine-readable output actually establish.
