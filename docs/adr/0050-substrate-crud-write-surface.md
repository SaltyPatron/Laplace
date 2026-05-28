# ADR 0050: SubstrateCRUD — the shared substrate write surface

## Status

**Accepted** — 2026-05-24 (status confirmed 2026-05-28: `NpgsqlSubstrateWriter.ApplyAsync` shipped at `app/Laplace.SubstrateCRUD/Npgsql/NpgsqlSubstrateWriter.cs` with test coverage; `entities_exist_bitmap` SRF live in `extension/laplace_substrate/sql/11_entities_exist_bitmap.sql.in`. Outstanding work per FLOWS.md audit: `MerkleDedup.FilterNovel` P/Invoke wiring + trunk-shortcircuit invocation from writer + `ApplyStreamAsync` + LRU/bloom cache.)
**Authors:** Anthony Hart

## Context

The substrate has 10+ planned per-source decomposers (`UnicodeDecomposer`, `WordNetDecomposer`, `OMWDecomposer`, `UDDecomposer`, `WiktionaryDecomposer`, `TatoebaDecomposer`, `ConceptNetDecomposer`, `Atomic2020Decomposer`, `TreeSitterDecomposer`, plus the [composite `ModelDecomposer` per ADR 0043](0043-composite-decomposer-architecture.md) parameterized by `ContainerFormat × TensorDtypeDecoder × SemanticArchitectureDecomposer × ModalityBinder`). Each one produces [`SubstrateChange` intents per ADR 0049](0049-substrate-change-intent-type.md) and needs to write entities + physicalities + attestations to PostgreSQL.

Without a shared write surface, every decomposer reimplements:

- Batched existence-check queries (`WHERE id = ANY($1::bytea[])`)
- Bulk Npgsql binary COPY discipline per table
- FK-dependency ordering (entities first, then physicalities + attestations referencing them)
- `ON CONFLICT DO NOTHING` semantics per [RULES.md R5](../../RULES.md) (attestations are idempotent — same source asserting same tuple is a no-op)
- Race-tolerance for cross-decomposer shared-entity creation (UnicodeDecomposer creates `Latn` Script entity; ISODecomposer adds ISO 15924 meta-attestations to the same row; converges via content-addressing + `ON CONFLICT`)
- Transaction boundaries (per-intent atomicity, batched-commit modes for high-throughput runs)
- Local existence cache (LRU/bloom-filter for hot working sets — re-ingesting WordNet's 117K synsets sees the same text entities thousands of times)
- Partial-run resume (a multi-hour ingest that fails at hour 7 on intent N resumes at intent N+1)
- Per-intent observability (rows inserted, rows already-existed, round-trip count, transaction time)
- Trunk-to-leaf Merkle-short-circuit dedup walks per the 2026-05-24 conversation (*"deduplication is again trunk to leaf because top down means any true is true for all below... so we're saving so many round trips if we do this right"*)

That's N decomposers × ~10 cross-cutting concerns = 100s of duplicated implementation surfaces, each with its own bugs. This is the exact pattern [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md) + [ADR 0016](0016-reusable-helpers-discipline.md) prohibit.

The 2026-05-24 conversation crystallized this: *"the client can decompose text, structure all of the records, establish the proper sequencing, etc... all before ever talking to the database to see what real interactions need to happen of which should just be simple CRUD orchestration we can optimize the generalize the fuck out of across the repo without reinventing the wheel a trillion times."*

## Decision

**Introduce `SubstrateCRUD` as the single shared substrate-write surface — one C# library (`Laplace.SubstrateCRUD` project per [ADR 0026](0026-csharp-project-structure.md)) implementing `Apply(SubstrateChange intent)` and consumed by every per-source decomposer without exception.**

### Public surface

```csharp
public interface ISubstrateWriter {
    /// <summary>
    /// Apply a SubstrateChange intent: trunk-to-leaf Merkle-shortcircuit
    /// dedup walk + per-table bulk Npgsql binary COPY of novel rows, all
    /// in one transaction. Idempotent on repeat (same intent = no-op
    /// after first application).
    /// </summary>
    Task<SubstrateCRUDResult> ApplyAsync(SubstrateChange intent, CancellationToken ct = default);

    /// <summary>
    /// Apply a stream of SubstrateChange intents; checkpoints periodically
    /// for resume. Used by long-running ingest runs (UnicodeDecomposer's
    /// 1.114M codepoint seed, full Wiktionary, model-vocab ingest at
    /// 150K+ tokens, etc.).
    /// </summary>
    Task<SubstrateCRUDResult> ApplyStreamAsync(
        IAsyncEnumerable<SubstrateChange> intents,
        SubstrateCRUDStreamOptions options,
        CancellationToken ct = default);
}

public sealed record SubstrateCRUDResult(
    int EntitiesAttempted,
    int EntitiesInserted,        // attempted - existing - on-conflict-no-op
    int PhysicalitiesAttempted,
    int PhysicalitiesInserted,
    int AttestationsAttempted,
    int AttestationsInserted,
    int RoundTripCount,          // SQL queries + COPY operations
    TimeSpan WallClock
);
```

### Algorithm (per intent)

```text
ApplyAsync(intent):
    BEGIN TRANSACTION;

    # Phase 1 — trunk-to-leaf Merkle short-circuit dedup walk
    # (Optimized: local LRU/bloom cache of recently-seen IDs may short-circuit
    # before the round-trip if the intent's IDs are all locally known.)

    candidate_entity_ids      = [r.Id for r in intent.Entities]
    candidate_physicality_ids = [r.Id for r in intent.Physicalities]
    candidate_attestation_ids = [r.Id for r in intent.Attestations]

    existing_entity_ids = SELECT id FROM entities
                          WHERE id = ANY(candidate_entity_ids::bytea[])
    novel_entities = [r for r in intent.Entities if r.Id not in existing_entity_ids]

    existing_physicality_ids = SELECT id FROM physicalities
                               WHERE id = ANY(candidate_physicality_ids::bytea[])
    novel_physicalities = [r for r in intent.Physicalities if r.Id not in existing_physicality_ids]

    existing_attestation_ids = SELECT id FROM attestations
                               WHERE id = ANY(candidate_attestation_ids::bytea[])
    novel_attestations = [r for r in intent.Attestations if r.Id not in existing_attestation_ids]

    # Phase 2 — bulk Npgsql binary COPY in FK-dependency order
    # If novel_entities is empty, skip the COPY entirely (no round-trip).
    if novel_entities:
        COPY entities (id, tier, type_id, first_observed_by, created_at)
            FROM STDIN BINARY  -- novel_entities

    if novel_physicalities:
        COPY physicalities (id, entity_id, source_id, kind, coord,
                            hilbert_index, trajectory, alignment_residual,
                            source_dim, observed_at)
            FROM STDIN BINARY  -- novel_physicalities

    if novel_attestations:
        COPY attestations (id, subject_id, kind_id, object_id, source_id,
                           context_id, rating, rd, volatility,
                           last_observed_at, observation_count)
            FROM STDIN BINARY  -- novel_attestations

    # Phase 3 — local cache update + observability
    cache.Insert(novel_entity_ids + novel_physicality_ids + novel_attestation_ids)
    emit_metrics(intent.Metadata, attempted, inserted, round_trips, wall_clock)

    COMMIT;
    return SubstrateCRUDResult(...);
```

### Round-trip count per intent

- **Best case (intent fully duplicate, trunk hash already in substrate)**: 1 round-trip total. The first existence-check on entities returns *all* candidate entity IDs as already-existing → novel_entities is empty → physicality + attestation existence checks also empty (no novel parent entities means no novel physicalities or attestations referencing them) → zero COPYs. The intent is a no-op. Cost: ~1ms.
- **Intent fully novel**: 6 round-trips (3 existence checks + 3 bulk COPYs). Independent of intent size — a 100-row intent and a 100,000-row intent cost the same number of round-trips, just different COPY stream sizes.
- **Intent partially novel** (common case for ConceptNet, Wiktionary cross-source ingest): 6 round-trips with smaller COPY streams. The existence check filters out shared rows.
- **With local cache warm**: existence checks may short-circuit to 0 round-trips (cache hit on all candidates → skip the SQL → if cache says "all known", skip COPYs). Round-trip count drops to 0 for hot working sets. Cache invalidation on rare cross-process inserts handled via PG `LISTEN`/`NOTIFY` or short cache TTL — implementation detail.

### Transaction modes

- **`Default`**: one transaction per `Apply(intent)`. ACID semantics per intent. Safest, mildly slower (more commit overhead).
- **`BatchedCommit(intent_count)`**: many intents per transaction, commit every N. For high-throughput decomposers (UnicodeDecomposer streaming 1.114M codepoint entities; ModelDecomposer streaming a 150K-token vocab). Tradeoff: larger rollback window on failure, but ~5-10× higher throughput.
- **`ReadCommittedSnapshot`** + **`Serializable`** isolation level choices documented per concrete decomposer's needs.

### Local existence cache

LRU + bloom filter combined. Bloom filter for fast "definitely not in cache" rejection; LRU for the actual cached IDs. Size tunable per decomposer (UnicodeDecomposer benefits from a tiny cache because every codepoint is touched once; WiktionaryDecomposer benefits from a large cache because common English words recur in every entry).

Cache writes happen after successful COPY commit. Cache invalidation across processes (e.g., two decomposer instances running in parallel) handled by PG `LISTEN`/`NOTIFY` or by cache TTL — picked per deployment.

### Checkpoint/resume

`ApplyStreamAsync` writes a journal file (`checkpoint.bin` next to the source data) recording `(intent_id, applied_at)` per successful intent. On resume, reads the journal, skips to the last applied intent, continues. Crash at hour 7 of a 10-hour run → resume at hour 7+1-intent on next invocation. Journal format: append-only length-prefixed records, atomic via `fsync` after each batched-commit boundary.

### Observability

Standardized metrics per intent (Prometheus-compatible — `Laplace.SubstrateCRUD` emits per [STANDARDS.md Logging](../../STANDARDS.md)):

- `laplace_crud_entities_attempted_total{source=X}`
- `laplace_crud_entities_inserted_total{source=X}`
- `laplace_crud_dedup_hit_ratio{source=X}` (existing / attempted)
- `laplace_crud_round_trips_per_intent{source=X}`
- `laplace_crud_wall_clock_seconds{source=X, phase=existence_check|copy|commit}`
- `laplace_crud_cache_hit_ratio{source=X}`

Per-source labels let one Grafana dashboard tell you which decomposer is dominating throughput.

### What `SubstrateCRUD` does NOT do

- Compute hashes ([HashComposer / ADR 0048](0048-hash-composer-leaf-to-trunk.md) does that, decomposer-side, before the intent is built).
- Decompose text ([TextDecomposer / ADR 0047](0047-text-decomposer-pure-primitive.md) does that).
- Build attestations (per-source decomposer's job — `SubstrateCRUD` only writes whatever's in `intent.Attestations`).
- Run cascade reads ([cascade SRF per ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md) is the read surface; `SubstrateCRUD` is the write surface only).
- Migrate schema ([DbUp + extension SQL per ADR 0021 + ADR 0023](0021-dbup-for-migrations.md) handle that; `SubstrateCRUD` runs against a substrate whose schema is already current).
- Decide what to insert (decomposer-side decision; `SubstrateCRUD` just executes the intent).
- Talk to PG for any reason other than substrate CRUD (no `SELECT laplace_cascade(...)` from `SubstrateCRUD` — that's a different surface).

### Placement

- **Project**: `Laplace.SubstrateCRUD` per [ADR 0026](0026-csharp-project-structure.md). New C# project under `app/Laplace.SubstrateCRUD/`. References `Npgsql` + `Laplace.Engine.Core` (for Hash128 type interop).
- **Test project**: `Laplace.SubstrateCRUD.Tests` with `Testcontainers.PostgreSql` per [STANDARDS.md Testing](../../STANDARDS.md). Tests spin a `postgis/postgis:18` container, install both extensions via `Laplace.Migrations`, then exercise `Apply()` against the live substrate. Verifies: idempotency on repeated apply, race-tolerance (concurrent applies of overlapping intents converge), partial-run resume (kill mid-stream, restart, verify state).
- **Consumed by**: every `IDecomposer` implementation. The `Laplace.Decomposers.Abstractions` project (introduced by a future ADR for the `IDecomposer` C# contract) wires `SubstrateCRUD` as a constructor dependency; per-source decomposers receive an `ISubstrateWriter` and never see `Npgsql` directly.

## Consequences

- **No per-source bespoke insert code anywhere in the substrate.** All decomposers go through `SubstrateCRUD.ApplyAsync()`. Bug in CRUD semantics → fix once → all decomposers benefit.
- **Performance optimization happens in one place.** Bulk COPY tuning, transaction batching, cache sizing, parallel COPY-per-table, prepared-statement reuse — one optimization surface, not N.
- **Race tolerance for cross-decomposer shared entities is automatic.** UnicodeDecomposer creating `walk` text entity + ModelDecomposer.TextModality creating `walk` text entity in parallel: one wins the insert, the other no-ops via `ON CONFLICT`. No coordination logic needed in either decomposer.
- **Checkpoint/resume becomes a substrate-wide capability, not per-decomposer effort.** Adding resume to a new decomposer = use `ApplyStreamAsync` instead of `ApplyAsync`. Done.
- **PG remains a dumb columnar store per [RULES.md R6](../../RULES.md).** No business logic in stored procedures. No PL/pgSQL workflows. Just batched queries + bulk COPY against schema owned by the extensions.
- **The substrate's write throughput becomes measurable + optimizable.** Standardized metrics let you see "WiktionaryDecomposer's dedup hit ratio dropped from 80% to 30% — something changed in the source data" or "ModelDecomposer's wall_clock spiked — likely the local cache evicted hot tokens".
- **Adding a new core table (currently not planned per [ADR 0002](0002-three-tables-no-event-log.md), but if future ADR adds one) requires extending `SubstrateCRUD`.** Single point of change.

## Alternatives considered

- **Each decomposer writes its own Npgsql logic.** Rejected — duplication anti-pattern per ADR 0016 + STANDARDS.md. N implementations, N drift surfaces, N bug-fix sites.
- **`SubstrateCRUD` exposes individual CRUD methods (`InsertEntity`, `InsertPhysicality`, `InsertAttestation`).** Rejected — per-row API leaks the batching concern to callers. Callers would re-invent batching badly. The intent-based API (`Apply(SubstrateChange)`) puts the batching responsibility in the right place.
- **PG-side stored procedures for the CRUD operations.** Rejected — violates [RULES.md R6](../../RULES.md) (PG doesn't do business logic). Also makes cross-language consistency testing harder (C# tests have to span the SPI boundary instead of mocking at the Npgsql layer).
- **gRPC-based intent stream to a separate substrate-write service.** Rejected for v0.1 — adds an operational moving part (service deployment, health checks, load balancing) for no win at single-process throughput levels. Substrate-write service is a possible v0.2+ option if/when multi-process parallel ingest hits coordination limits.
- **One transaction per row.** Rejected — pathologically slow. Defeats the whole batched-CRUD purpose. ~10ms per row × millions of rows = days per ingest.
- **No transaction; rely on PK uniqueness for atomicity.** Rejected — physicalities + attestations have FKs to entities. A failure between the entity COPY and the physicality COPY leaves dangling references. Per-intent transaction is the smallest correct atomicity boundary.

## References

- [RULES.md R2](../../RULES.md) — three tables, no event log
- [RULES.md R5](../../RULES.md) — attestation idempotency (`ON CONFLICT DO NOTHING` semantics)
- [RULES.md R6](../../RULES.md) — DB as dumb columnar store; CRUD does no compute
- [RULES.md R7](../../RULES.md) — determinism by construction
- [RULES.md R16](../../RULES.md) — separation of concerns
- [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md)
- [STANDARDS.md ID discipline](../../STANDARDS.md) — raw bytes only
- [STANDARDS.md Testing](../../STANDARDS.md) — xUnit + Testcontainers
- [STANDARDS.md Logging](../../STANDARDS.md) — structured logging
- [DESIGN.md I — Schema](../../DESIGN.md) — three core tables `SubstrateCRUD` writes
- [ADR 0002](0002-three-tables-no-event-log.md) — three tables
- [ADR 0011](0011-polymorphic-plugin-architecture.md) — polymorphic plugin architecture
- [ADR 0016](0016-reusable-helpers-discipline.md) — reusable helpers
- [ADR 0021](0021-dbup-for-migrations.md) — DbUp + Npgsql
- [ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md) — schema lifecycle owned by extension; CRUD writes against current schema
- [ADR 0024](0024-engine-modularization.md) — engine modularization
- [ADR 0025](0025-pg-extension-modularization.md) — PG extension modularization
- [ADR 0026](0026-csharp-project-structure.md) — `Laplace.SubstrateCRUD` placement
- [ADR 0027](0027-separation-of-concerns-invariants.md) — separation of concerns invariants
- [ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md) — cascade SRF (the READ symmetry partner)
- [ADR 0045](0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md) — `laplace_admin` is SUPERUSER (no SECURITY DEFINER wrapper)
- [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md)
- [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md)
- [ADR 0049 SubstrateChange intent type](0049-substrate-change-intent-type.md) — the type `SubstrateCRUD.Apply` consumes
- Conversation 2026-05-24: client-does-all-work + CRUD-as-shared-primitive ("we can optimize and generalize the fuck out of across the repo without reinventing the wheel a trillion times").
- Conversation 2026-05-24: trunk-to-leaf Merkle-shortcircuit dedup pattern ("deduplication is again trunk to leaf because top down means any true is true for all below").
