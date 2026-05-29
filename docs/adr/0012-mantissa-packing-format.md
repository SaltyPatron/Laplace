# ADR 0012: Mantissa-packing format — 212-bit per-vertex payload, XYZ=entity_id + M=metadata

## Status

**Accepted** — 2026-05-21
**Revised** — 2026-05-23

- Supersedes the original 8-tier / 12-position / 60-truncated-hash layout. That rationale conflated trajectory vertices (metadata containers with no per-vertex spatial meaning) with the canonical-coord layer of `entities` (where 4D position WAS load-bearing under the original schema). The revised layout uses every available significand bit; truncating IDs violated STANDARDS.md ID discipline.
- Aligned with the schema reorganization: trajectory is a column on `physicalities` (specifically on CONTENT-kind physicalities), NOT on `entities`. Each vertex's mantissa-packed `entity_id` references another row in `entities` — same hash space throughout.

## Context

A T≥1 entity's content is recorded as the trajectory of its **CONTENT-kind physicality** — a 4D `ST_LineString` whose ordered vertices reference the entity's constituents in this source's decomposition (each constituent itself an entity, same hash space; per GLOSSARY "Entity" — entities play content AND building-block roles simultaneously). The trajectory records *occurrence/order/composition* — which constituent entities compose this one, in what order — by walking vertices in order and resolving each `entity_id` to its row in `entities`, down to the Merkle stratum of T0 Unicode codepoints.

> **Scope note (per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth #6).** This trajectory mechanic is the *content-composition* record for textual/compositional decomposition (codepoint n-grams up the Merkle DAG). It is **not** a bit-perfect blob store and reconstructing the original artifact byte-for-byte is **not** a goal — bit-perfect preservation only returns the file you already had. Model ingestion in particular does **not** flow through this content-trajectory path: per truths #1–#2 a model is a streaming O(params) ETL of weight cells into Glicko-2 matchup observations (the consensus rating is stored; the weights and the blob are discarded). Do not read this ADR as defining a model-reconstruction codec.

Per-source decomposition rules apply: the substrate-canonical source emits CONTENT physicalities with UAX#29 decomposition for text, pixel-grid decomposition for images, etc.; external compositional sources may emit their own CONTENT physicalities with their own decomposition (BPE token n-grams, lexical structure, ...). All compositional sources inhabit the same trajectory mechanic with the same mantissa-pack layout.

Each vertex must carry:

- The **full 128-bit `entity_id`** of the referenced entity (so the row is recoverable by content-addressed lookup — no truncation per STANDARDS.md ID discipline).
- The **ordinal** of this vertex in the LineString's sequence (1-indexed; mirrored by `ST_PointN` but stored explicitly for robustness against geometry transforms).
- The **run length** for RLE compression of consecutive identical entity references (e.g., repeated graphemes, padding spaces, pixel runs).
- A pool of **reserved flag bits** for future use (modality markers, continuation/chain bits, visualization/filtering tags, indexing hints).

Storage budget per vertex: 4 × FP64 = 256 bits total. To keep every emitted coord a finite normal double (PG-geometry-valid, no NaN/inf), we pin each component's biased exponent to `0x3FF` (magnitude in `[1, 2)`). That sacrifices 4 × 11 = 44 bits of exponent storage to retain validity; the remaining 4 × (1 sign + 52 mantissa) = **212 bits** carry the payload.

## Decision

Per-vertex 212-bit payload, distributed:

```
entity_id        : 128 bits   full BLAKE3-128 of the referenced entity (no truncation)
ordinal          :  16 bits   uint16 — position in trajectory (1-indexed)
run_length       :  16 bits   uint16 — RLE count of consecutive identical references
flags            :  52 bits   reserved (modality, continuation, visualization, indexing, ...)
                   ──────
                    212 bits — uses every available significand bit
```

Distribution across the four FP64 components — **XYZ = entity_id, M = metadata**:

| Component | 53-bit slot contents |
|---|---|
| X | `entity_id` bits 0..52 (low 53 of `hash.lo`) |
| Y | `entity_id` bits 53..105 (high 11 of `hash.lo` + low 42 of `hash.hi`) |
| Z | `entity_id` bits 106..127 (high 22 of `hash.hi`) + `flags` bits 0..30 (31 bits) |
| M | `ordinal` (16) + `run_length` (16) + `flags` bits 31..51 (21 bits) |

Each component's 53-bit slot = (sign bit + 52 mantissa bits); biased exponent pinned to `0x3FF` so each coord lies in `[1, 2) ∪ (-2, -1]`.

Function signatures (`engine/core/include/laplace/core/mantissa.h`):

```c
typedef struct {
    hash128_t entity_id;     /* 128 bits — full BLAKE3-128, no truncation */
    uint16_t  ordinal;
    uint16_t  run_length;
    uint64_t  flags;         /* low 52 bits used; high 12 MUST be zero */
} mantissa_payload_t;

void mantissa_pack(double vertex[4], const mantissa_payload_t* p);
void mantissa_unpack(const double vertex[4], mantissa_payload_t* out);
```

There is no `base` parameter. Trajectory vertices are not spatial points being annotated; they are metadata containers whose entire bit pattern is determined by the payload.

## Consequences

- **Full ID, no collision math.** STANDARDS.md ID discipline holds end-to-end. The original "~10⁻⁹ collision per pair within a trajectory" footnote of the truncated design is gone — entity references are exact.
- **Same entity → identical XYZ bit pattern** across every trajectory vertex referencing it (a *hash-bit-pattern scatter*). The substrate's reference topology is directly visualizable in 3D (XYZ ∈ `[1, 2)³ ∪ (-2, -1]³`); M is a scalar channel for filtering / coloring / labeling. This is **distinct from** plotting the entity's canonical-coord position or its physicality coords — those are semantic placements; the mantissa-XYZ scatter is purely an ID-bit-pattern view of reference frequency.
- **52 reserved flag bits** available for substrate-wide policy without re-layout: modality markers, continuation bits (chained vertices for runs > 65535), visualization/coloring channels, structural-index opclass hints.
- **`physicalities.trajectory` GIST index needs replacement.** Every mantissa-packed trajectory collapses into the `[1, 2)⁴ ∪ (-2, -1]⁴` bounding box, so `gist_geometry_ops_nd` filters nothing. Replacement is a structural opclass over (entity_id range, ordinal range, run_length filter) — tracked separately.
- **Determinism.** Pack/unpack is pure bit arithmetic; no FP rounding involved in the data path. `mantissa_unpack(mantissa_pack(p)) == p` holds bit-identically across runs and machines — this is determinism *of the payload codec for these four doubles*, not a claim that any source artifact is reconstructed bit-perfectly (which is a non-goal per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth #6).
- **Uniform across modalities.** Same mantissa-pack mechanic at every tier of every modality — text trajectories, pixel/patch/region/image trajectories, audio frame/track trajectories, model-tokenizer trajectories. The decomposition rule per source/type differs; the vertex layout does not.

## Alternatives considered

- **Parallel `bytea[]` column** of full 128-bit IDs per vertex + side `int[]` for ordinals + `int[]` for run_lengths. Cleanly typed; loses the "trajectory is self-contained PG geometry" property; requires `unnest` joins to enumerate.
- **Side `vertex_metadata` table** keyed `(physicality_id, ordinal)`. Two storage surfaces; more I/O at cascade read; not preferred.
- **Keep the old 60-bit truncated hash** and rely on entity-table lookup to disambiguate near-collisions. Violates ID discipline; complicates the trajectory→entity resolver; no benefit.

## References

- [GLOSSARY.md](../../GLOSSARY.md) — "Entity" (dual role), "Mantissa packing", "Trajectory", "Physicality", "Universal T0"
- [STANDARDS.md](../../STANDARDS.md) — "ID discipline", "Datatype standards" (RLE length, constituent count, tier), "Canonicalization discipline"
- [DESIGN.md](../../DESIGN.md) — §I (schema), §III (SQL helpers), §IV (engine API)
- [ADR 0015](0015-blake3-for-entity-hashing.md) — BLAKE3-128 entity ID, raw-bytes discipline
- [ADR 0005](0005-hilbert-over-hyperbox.md) — Hilbert is a 1D index over the physicality-coord layer; distinct from mantissa-pack
- [RULES.md](../../RULES.md) — R18 doc currency; R22 reuse-don't-reinvent; R9 no-corner-cutting
