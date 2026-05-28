# ADR 0049: SubstrateChange — the unified intent type between decomposers and SubstrateCRUD

## Status

**Accepted** — 2026-05-24 (status confirmed 2026-05-28: `SubstrateChange` shipped at `app/Laplace.SubstrateCRUD/SubstrateChange.cs` with test coverage)
**Authors:** Anthony Hart

## Context

Every per-source decomposer (`UnicodeDecomposer`, `WordNetDecomposer`, `OMWDecomposer`, `UDDecomposer`, `WiktionaryDecomposer`, `TatoebaDecomposer`, `ConceptNetDecomposer`, `Atomic2020Decomposer`, `TreeSitterDecomposer`, `ModelDecomposer` composite per [ADR 0043](0043-composite-decomposer-architecture.md)) produces some quantity of new substrate state per unit of source content ingested — a batch of entities, a batch of physicalities, a batch of attestations. The 2026-05-24 conversation surfaced that this output is fundamentally one **change intent** per per-source-content-unit, fully prepared client-side after [TextDecomposer (ADR 0047)](0047-text-decomposer-pure-primitive.md) + [HashComposer (ADR 0048)](0048-hash-composer-leaf-to-trunk.md) + source-specific attestation building have run.

That intent then gets handed to the shared [`SubstrateCRUD` write surface (ADR 0050)](0050-substrate-crud-write-surface.md) which does the trunk-to-leaf Merkle-short-circuit dedup + bulk COPY against PostgreSQL.

Without a documented shared intent type:

- Every decomposer invents its own input shape to `SubstrateCRUD`. Coupling between decomposers and CRUD becomes N pairwise contracts instead of one.
- Streaming + checkpoint/resume for multi-hour ingest runs (37 GB Unicode, 49 MB WordNet, 4.3 GB UD-Treebanks, 34 GB Wiktionary, 9.5 GB ConceptNet, 125 GB DeepSeek-Coder-33B, etc.) has no canonical serialized representation. The "fails at hour 7, resume at intent N+1" recovery story requires intents to be serializable + replayable.
- Cross-decomposer entity sharing (WordNetDecomposer creates `walk` text entity at BLAKE3("walk"); ModelDecomposer.TextModality also creates `walk` text entity at BLAKE3("walk"); both must converge to the same row through ON CONFLICT DO NOTHING) has no specified data contract.
- Observability + per-intent metrics + intent-level transaction boundaries have no shared shape to attach to.

Per [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md) and the 2026-05-24 conversation excerpt — *"we can optimize and generalize the fuck out of across the repo without reinventing the wheel a trillion times"* — the intent shape needs to be defined once, used by every decomposer.

## Decision

**Introduce `SubstrateChange` as the canonical intent type — a single C# record (with C-ABI struct equivalent for engine-side construction) carrying the full set of substrate state changes a decomposer wants to apply.**

### Shape

```csharp
// C# representation (canonical):
public sealed record SubstrateChange(
    IReadOnlyList<EntityRow> Entities,
    IReadOnlyList<PhysicalityRow> Physicalities,
    IReadOnlyList<AttestationRow> Attestations,
    SubstrateChangeMetadata Metadata
);

public sealed record EntityRow(
    Hash128 Id,                          // BLAKE3-128 of canonical content per ADR 0015
    byte Tier,                           // 0..255 per DESIGN.md I; smallint in PG
    Hash128 TypeId,                      // FK to entities(id) — the type entity
    Hash128? FirstObservedBy             // FK to entities(id) — source; nullable for bootstrap
    // created_at is set by SubstrateCRUD at insert time, not by decomposer
);

public sealed record PhysicalityRow(
    Hash128 Id,                          // BLAKE3-128 of canonical (entity_id, source_id, kind, coord, trajectory)
    Hash128 EntityId,                    // FK to entities(id)
    Hash128 SourceId,                    // FK to entities(id) — sources are entities
    short Kind,                          // 1=CONTENT, 2=BUILDING_BLOCK, 3=PROJECTION per DESIGN.md I
    Point4D Coord,                       // 4D point (X,Y,Z,M); engine math kernel format
    Hash128 HilbertIndex,                // 128-bit Hilbert position
    LineString4D? Trajectory,            // nullable; mantissa-packed per ADR 0012
    double? AlignmentResidual,           // nullable; PROJECTION kind carries Procrustes residual
    int? SourceDim                       // nullable; per-source ingestion dimensionality
    // n_constituents derived from Trajectory.NumPoints at insert
    // observed_at set by SubstrateCRUD
);

public sealed record AttestationRow(
    Hash128 Id,                          // BLAKE3-128 of (subject_id, kind_id, object_id, source_id, context_id)
    Hash128 SubjectId,                   // FK to entities(id)
    Hash128 KindId,                      // FK to entities(id) — attestation-kind entity
    Hash128? ObjectId,                   // FK to entities(id); nullable for unary kinds
    Hash128 SourceId,                    // FK to entities(id)
    Hash128? ContextId,                  // FK to entities(id); nullable for context-free
    long Rating,                         // Glicko-2 mu, fixed-point ×10^9 per ADR 0004
    long Rd,                             // RD, fixed-point ×10^9
    long Volatility,                     // volatility, fixed-point ×10^9
    long ObservationCount                // initial 1 unless source provides batched observations
    // last_observed_at set by SubstrateCRUD at insert time
);

public sealed record SubstrateChangeMetadata(
    Hash128 SourceId,                    // emitting source (decomposer's source entity)
    string SourceContentUnitName,        // human-readable for logs/observability ("WordNet synset cat.n.01", "Qwen3 vocab token #12345")
    DateTimeOffset BuiltAt,              // when the intent was constructed
    Hash128? ParentIntentId               // for checkpoint/resume: prior intent in this run
);
```

### FK-dependency ordering invariant

Within a single `SubstrateChange`:

1. **`Entities` must topologically precede dependents.** Type entities, source entities, and any meta-attestations' subject entities must appear in `Entities` *before* the rows that reference them (or already exist in substrate from a prior intent).
2. **`Physicalities` reference entities** via `EntityId` + `SourceId`. Both must already exist (either in `Entities` of this intent, or in substrate from a prior commit).
3. **`Attestations` reference entities** via `SubjectId` + `KindId` + `ObjectId` + `SourceId` + `ContextId`. All must already exist.

This invariant lets `SubstrateCRUD` insert in fixed FK order: `entities` first, then `physicalities`, then `attestations`. Each list is independently sortable within itself (entities by tier ascending, etc.) but the table-order is fixed.

### Serialization for checkpoint/resume

`SubstrateChange` is serializable to a stable binary representation (length-prefixed `bytea` fields, native order for `int`/`long`/`double`, fixed schema with per-field type tags). A multi-hour ingest run that fails at intent N can be resumed at intent N+1 by replaying the serialized intent stream from the checkpoint file. The `Metadata.ParentIntentId` chains intents for ordered replay.

Concrete serialization format deferred to the implementation Story (Protocol Buffers, MessagePack, or substrate-native length-prefixed bytea — TBD by perf measurement). The ADR locks in the *requirement* of serializability, not the specific wire format.

### Content-addressed IDs computed by decomposer, not by SubstrateCRUD

All `Hash128` ID fields are **content-addressed by the decomposer** (via [HashComposer / ADR 0048](0048-hash-composer-leaf-to-trunk.md) for entities + physicalities; via direct BLAKE3 of the canonical attestation 5-tuple for attestations). `SubstrateCRUD` never computes IDs — it only reads them, checks existence, and inserts. Per [RULES.md R6](../../RULES.md) all entity math is engine-side at INSERT time, not DB-side.

### No PG-side compute beyond the Glicko-2 aggregate

`SubstrateCRUD` consumes `SubstrateChange` and writes via batched `WHERE id = ANY($1::bytea[])` queries + bulk Npgsql binary COPY. PG does no math, no derivation, no canonicalization. The intent arrives fully-baked.

### Where it lives

- **C# canonical type**: `Laplace.SubstrateCRUD` project per [ADR 0026](0026-csharp-project-structure.md) (new project introduced by [ADR 0050](0050-substrate-crud-write-surface.md)).
- **Engine-side equivalent struct (for engine-side intent construction in cascade-time scenarios)**: header at `engine/core/include/laplace/core/substrate_change.h`. C-ABI struct with arrays + lengths; opaque-handle alternative if the struct shape evolves.
- **PG-side awareness**: none directly. The PG extension doesn't see `SubstrateChange` — it sees the batched queries + COPY streams that `SubstrateCRUD` issues.

## Consequences

- **One contract between every decomposer and the CRUD layer.** Adding a new decomposer = build a `SubstrateChange` and call `SubstrateCRUD.Apply()`. No bespoke insert paths.
- **Checkpoint/resume becomes mechanical.** Serialize intents to a journal file; on resume, skip to the last-known-applied intent ID; replay from there. Multi-hour ingest runs survive crashes.
- **Race-tolerant cross-decomposer entity sharing falls out of content-addressing.** Two decomposers concurrently building intents that both contain an `EntityRow` with `Id = BLAKE3("walk")` is fine — one insert wins, the other no-ops via `ON CONFLICT DO NOTHING`. The intent doesn't need to coordinate.
- **Observability primitives attach to one type.** Per-intent metrics (entity count, physicality count, attestation count, novel-vs-existing ratio, round-trip count, transaction time) emit against `SubstrateChangeMetadata.SourceContentUnitName`. One dashboard tells you what's happening regardless of source.
- **The intent type is the public API of the ingestion pipeline.** Changes to it are breaking for every decomposer. Versioning the binary serialization format becomes part of the substrate's compatibility contract.
- **Per-row insert metadata (created_at, observed_at, last_observed_at) is set by `SubstrateCRUD`, NOT the decomposer.** Decomposers don't get to forge timestamps. This keeps the audit trail honest and removes one source of cross-decomposer drift.

## Alternatives considered

- **No intent type; each decomposer calls Npgsql directly.** Rejected — N implementations of bulk COPY + transaction discipline + existence checking + ON CONFLICT semantics + race tolerance. Exact pattern STANDARDS.md and ADR 0016 forbid.
- **Pass the populated TierTree directly to `SubstrateCRUD`.** Rejected — the TierTree carries only entity + physicality state. Attestations come from the per-source decomposer's source-specific knowledge, not from the tier structure. Two separate concerns; one combined intent type is cleaner than overloading TierTree.
- **Per-table intent types (`EntityChange`, `PhysicalityChange`, `AttestationChange`).** Rejected — fragments the unit-of-atomicity. A WordNet synset ingest creates entities (for the synset + lemmas) + physicalities + attestations all together; splitting into three separate calls breaks transactional atomicity.
- **Streaming intent (decomposer pushes rows as it produces them).** Rejected for v0.1 — atomicity is harder, partial-failure semantics are harder. Per-intent-batched is the right shape for most decomposers (one intent per WordNet synset, one intent per Wiktionary entry, one intent per model-tokenizer-vocab-chunk). Streaming intent is a possible v0.2+ optimization for multi-GB single-content-units (a whole-model intent for Qwen3-480B would be many GB; the streaming variant becomes worth designing then).
- **JSON serialization for checkpoint files.** Rejected — `bytea`-heavy intents (every Hash128 is 16 raw bytes; every Coord is 4 doubles) serialize 3-5× slower as base64-in-JSON than as raw binary. Per [STANDARDS.md ID discipline](../../STANDARDS.md) ("Raw bytes only — never hex/text") JSON would also force base64 round-trip ceremony for every ID. Binary serialization wins on both axes.

## References

- [RULES.md R2](../../RULES.md) — three tables, no event log
- [RULES.md R5](../../RULES.md) — attestation idempotency (informs `SubstrateCRUD`'s ON CONFLICT semantics; intent shape doesn't change)
- [RULES.md R6](../../RULES.md) — DB as dumb columnar store; decomposer computes all IDs before INSERT
- [RULES.md R16](../../RULES.md) — separation of concerns (math in engine; orchestration in C#)
- [STANDARDS.md ID discipline](../../STANDARDS.md) — raw bytes only
- [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md)
- [STANDARDS.md Datatype standards](../../STANDARDS.md) — int64 fixed-point Glicko-2, bytea(16) IDs
- [GLOSSARY.md Attestation Tuple Shape](../../GLOSSARY.md)
- [DESIGN.md I — Schema](../../DESIGN.md) — three core tables this intent populates
- [ADR 0002](0002-three-tables-no-event-log.md) — three tables (intent matches the table count)
- [ADR 0004](0004-int64-fixed-point-glicko2.md) — Glicko-2 fixed-point (intent's Rating/Rd/Volatility types)
- [ADR 0011](0011-polymorphic-plugin-architecture.md) — polymorphic plugin architecture
- [ADR 0012](0012-mantissa-packing-format.md) — mantissa packing (intent's `Trajectory` field)
- [ADR 0015](0015-blake3-for-entity-hashing.md) — BLAKE3-128 (intent's `Id` fields)
- [ADR 0016](0016-reusable-helpers-discipline.md) — DRY
- [ADR 0021](0021-dbup-for-migrations.md) — DbUp (schema lifecycle separate from intent application)
- [ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md) — extension owns schema
- [ADR 0026](0026-csharp-project-structure.md) — C# project structure (`Laplace.SubstrateCRUD` placement)
- [ADR 0039](0039-schema-reorganization-entity-identity-vs-physicality-representation.md) — schema reorganization (intent shape mirrors the table shape post-reorg)
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap order (early intents are the substrate-canonical seeding)
- [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md)
- [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md) — fills in the Hash128 IDs the intent carries
- [ADR 0050 SubstrateCRUD write surface](0050-substrate-crud-write-surface.md) — consumes this intent type
- Conversation 2026-05-24: client-does-all-work + CRUD-orchestration-as-shared-primitive clarification.
