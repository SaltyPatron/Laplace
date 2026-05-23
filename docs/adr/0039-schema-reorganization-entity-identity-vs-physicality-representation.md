# ADR 0039: Schema reorganization — entity is identity, physicality is representation

## Status

**Accepted** — 2026-05-23

## Context

The original schema (per ADR 0002 + the initial DESIGN.md) carried geometry on the `entities` table: `canonical_coord`, `hilbert_index`, `trajectory`, `radius_origin`, `n_constituents`. That assumed the entity itself has a single authoritative 4D placement — one entity, one coord, one trajectory.

Conversation surfaced two structural problems with that assumption:

1. **An entity can have many representations.** The same `cat` row may have a substrate-canonical CONTENT view (UAX#29 decomposition, centroid-of-constituents coord), a BUILDING_BLOCK view (where it sits when used as a constituent across the corpus), and many PROJECTION views — one per AI model, linguistic resource, or external source that has observed it. Each carries its own coord and its own structural decomposition (BPE for an LLM tokenizer, lexical structure for WordNet, pixel grid for an image-modality entity, etc.). Storing only one as a column on `entities` privileges one source/lens over all others.
2. **Inference is A* through the typed attestation graph weighted by Glicko-2, not geometric search through S³.** Geometric structure (canonical coord, Hilbert, trajectory shape) is value-additive enrichment — accelerates candidate narrowing, supports visualization and modality clustering, gives radial-abstraction structure — but the engine is graph traversal of rated typed edges. Strip the geometric layer entirely and the substrate still functions. Therefore: identity (the entity row) should not be entangled with one specific geometric representation.

The schema needs to separate **identity** (one row per unique observed n-gram, content-addressed) from **representation** (per-source × per-kind 4D view + structural decomposition).

## Decision

`entities` is stripped to identity + tier + type + lightweight provenance:

```sql
CREATE TABLE entities (
    id                 bytea       PRIMARY KEY CHECK (octet_length(id) = 16),  -- BLAKE3-128 of canonical content
    tier               smallint    NOT NULL,
    type_id            bytea       NOT NULL REFERENCES entities(id),           -- declares what KIND of thing this is
    first_observed_by  bytea       REFERENCES entities(id),
    created_at         timestamptz NOT NULL DEFAULT now()
);
```

`physicalities` owns all geometric + structural per-source-per-kind representation:

```sql
CREATE TABLE physicalities (
    id                 bytea       PRIMARY KEY CHECK (octet_length(id) = 16),
    entity_id          bytea       NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    source_id          bytea       NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    kind               smallint    NOT NULL,                                   -- 1=CONTENT, 2=BUILDING_BLOCK, 3=PROJECTION (extensible)
    coord              geometry    NOT NULL,
    hilbert_index      bytea       NOT NULL,
    trajectory         geometry,                                               -- mantissa-packed LINESTRING; nullable
    radius_origin      double precision GENERATED ALWAYS AS (...) STORED,
    n_constituents     integer     NOT NULL DEFAULT 0,
    alignment_residual double precision,
    source_dim         integer,
    observed_at        timestamptz NOT NULL DEFAULT now(),
    UNIQUE (entity_id, source_id, kind)
);
```

`attestations` keeps its 5-FK shape (`subject_id`, `kind_id`, `object_id`, `source_id`, `context_id`) all referencing `entities(id)`; gets a content-addressed `id` PK as well (BLAKE3 of the 5-tuple) instead of the old bigserial.

Column-role naming: **`id` for PKs, `<role>_id` for FKs**. The values are still BLAKE3-128 hashes per STANDARDS.md ID discipline; the *column name* reflects the column's role, not the value's mechanism.

## Consequences

- **One identity, many lenses.** A single entity row can carry an unbounded number of typed views via `physicalities` rows. Adding a new source produces new physicality rows; the entity row is untouched.
- **Canonical placement is just another source's view.** The substrate-canonical source is an entity in `entities`; its physicalities for every entity provide the "canonical" 4D placement. External sources are first-class peers. Arena / Glicko-2 reconcile disagreement.
- **Inference is unblocked from geometry.** Cascade A* walks the typed attestation graph; physicality coord lookups accelerate candidate narrowing but are not load-bearing. Strip the geometric layer and traversal still works.
- **Per-source decomposition is captured.** A source's view of "how this entity breaks into constituents" lives on its CONTENT physicality's trajectory column. Different sources can have different decompositions for the same entity (UAX#29 vs BPE for `cat`; pixel-grid-row-major vs space-filling for an image).
- **Per-physicality content-addressed id.** A specific physicality can be referenced (e.g., `(some_attestation, IS_PROJECTION_ON, physicality_id)`) without an opaque surrogate key.
- **Indexes redistribute.** The geometric indexes (GIST on coord, btree on Hilbert, btree on radius_origin) move from `entities` to `physicalities`. `entities` retains only identity + tier + type indexes.
- **Bootstrap responsibility grows.** Install must seed: T0 codepoint entities + their substrate-canonical CONTENT physicalities; Entity Type entities (Text, Image, ...); substrate-canonical source entity; kind-name entities for physicality kinds and attestation kinds. Per ADR 0006 perfcache and DB seed sibling rule.
- **Migration cost.** Net-positive: the substrate is at Chunk 0/1 with no production data; the reorganization lands cleanly without backward-compat shims.

## Implications for previous ADRs

- **ADR 0002 (three tables, no event log)** stands — still three core tables. Schema shape inside each table changes.
- **ADR 0005 (Hilbert over hyperbox)** stands — Hilbert now lives on `physicalities`, indexing per-source per-kind coords. Hilbert is value-additive enrichment, not load-bearing for inference.
- **ADR 0012 (mantissa-packing)** — already revised in same batch; trajectory now lives on physicalities, mantissa-pack carries `entity_id` (not `entity_hash`).
- **ADR 0015 (BLAKE3-128)** stands — ID values are BLAKE3-128. Column-role name change (`hash` → `id`) is in STANDARDS.md ID discipline; the value-mechanism is unchanged.
- **ADR 0029 (custom indexing)** — opclass naming retains `hash128_ops` style for the underlying datatype; column-role-driven usage now references `id` / `*_id` columns.
- **ADR 0036 (arena semantics + source trust)** — strengthened by the explicit `physicality.kind` axis: arena semantics can now distinguish lens-specific attestations.

## Alternatives considered

- **Keep canonical_coord on entities + add typed projections elsewhere.** Privileges substrate-canonical over external sources; clutters the entity row with one representation; conflicts with multi-modal types where the "canonical" geometric placement depends on the type's decomposition rule.
- **Add type_id but keep geometry columns.** Solves multi-modality typing but not the multi-representation problem; still entangles identity with one geometric view.
- **Eliminate physicalities entirely; represent everything as attestations.** Loses the per-source 4D geometric structure that PostGIS is good at querying. Cascade benefits from physicality-coordinate candidate access; reducing it to pure attestation walks throws away that acceleration. Geometry is enrichment, not load-bearing — but enrichment that's worth keeping.

## References

- [DESIGN.md](../../DESIGN.md) §I (schema), §V (indexing), §IX (three-phase)
- [GLOSSARY.md](../../GLOSSARY.md) — "Entity", "Physicality", "Canonical coordinate", "Data Class"
- [STANDARDS.md](../../STANDARDS.md) — "ID discipline"
- [ADR 0002](0002-three-tables-no-event-log.md) — three-table invariant
- [ADR 0005](0005-hilbert-over-hyperbox.md) — Hilbert as candidate-narrowing tool
- [ADR 0012](0012-mantissa-packing-format.md) — mantissa-pack on physicality trajectory
- [ADR 0036](0036-arena-semantics-and-source-trust.md) — arena semantics
- [ADR 0040](0040-multi-modal-entity-types-universal-t0.md) — entity types and modality decomposition
