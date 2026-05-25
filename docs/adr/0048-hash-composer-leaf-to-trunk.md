# ADR 0048: HashComposer — leaf-to-trunk content-addressing primitive

## Status

**Proposed** — 2026-05-24
**Authors:** Anthony Hart

## Context

The substrate is a **semantic Merkle DAG** (per [GLOSSARY.md "Tiered Merkle DAG"](../../GLOSSARY.md)) — every entity's ID is BLAKE3-128 of its canonical content per [ADR 0015](0015-blake3-for-entity-hashing.md), and T≥1 entities compose their ID from their constituents' IDs via `hash128_merkle(tier, [child_ids], child_count)` — already implemented in [`engine/core/src/hash128.c`](../../engine/core/src/hash128.c).

Once a text input has been segmented into a `TierTree` by [TextDecomposer (ADR 0047)](0047-text-decomposer-pure-primitive.md) — or once any non-text decomposer (image patches, audio frames, code AST nodes from TreeSitter, etc.) has produced an equivalent tier hierarchy structure — the next step is **content-addressing the whole tree**:

- T0 leaves: look up codepoint ID + 4D coord + Hilbert index from the [perfcache](0006-perfcache-and-db-seed-siblings.md) (the L2/L3 cache-resident T0 atom table per [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) Stage 5).
- T≥1 nodes: compute ID via `hash128_merkle(tier, [constituent_ids])`; compute coord as centroid of constituents' coords (per [DESIGN.md VI](../../DESIGN.md) "For T≥1 entities: centroid of constituents' canonical coords"); compute Hilbert via `hilbert4d_encode(coord)`.

This must happen in **leaf-to-trunk** order because each tier's hash depends on its constituents' hashes already being computed. There's no shortcut: you cannot compute the trunk hash without first knowing the T0 leaves' hashes (perfcache lookup), then the T1 nodes' hashes (Merkle composition over T0 children), then T2 (over T1 children), and so on.

The 2026-05-24 conversation that surfaced this ADR clarified the three-direction architecture of the ingest pipeline:

1. **Decomposition is trunk-to-leaf** (TextDecomposer + analogous-per-modality decomposers — top-down parsing).
2. **Hash/coord/Hilbert is leaf-to-trunk** (this ADR — bottom-up content-addressing).
3. **Dedup is trunk-to-leaf** ([SubstrateCRUD per ADR 0050](0050-substrate-crud-write-surface.md) — Merkle short-circuit: if trunk hash exists in `entities`, the entire subtree is in substrate, no further DB work).

This second phase is what HashComposer owns. The first phase is structural decomposition; the third phase is membership/insert. HashComposer is the bridge — pure compute, zero DB interaction, fully client-side.

Without a named primitive, every per-source decomposer reimplements its own version of "walk the tier tree bottom-up, hash each node, centroid the coords, encode the Hilbert" — which is exactly the duplication anti-pattern [STANDARDS.md "Reusable helpers"](../../STANDARDS.md) and [ADR 0016](0016-reusable-helpers-discipline.md) forbid. Per the 2026-05-24 conversation: *"we can optimize and generalize the fuck out of across the repo without reinventing the wheel a trillion times."*

## Decision

**Introduce `HashComposer` as a pure leaf-to-trunk compute primitive that consumes a `TierTree` (or any equivalent tier-structured input) and produces a populated `TierTree` with `id` + `coord` + `hilbert_index` filled in at every node.**

### Contract

**Input**: A `TierTree` with parent/child structural links per tier, with leaves carrying their atomic content (codepoint values for text-derived trees; pixel values for image-derived trees; sample values for audio; AST node types for code; etc.) but no IDs / coords / Hilbert indices yet computed.

**Algorithm** (deterministic, pure, stateless, leaf-to-trunk):

```text
for each leaf node L (T0):
    if L is a codepoint:
        (id, coord, hilbert) = codepoint_table_lookup(L.codepoint)   # perfcache lookup
    else if L is a pixel:
        (id, coord, hilbert) = pixel_atom_lookup(L.rgb)              # analogous per-modality
    else if L is an audio sample:
        (id, coord, hilbert) = audio_sample_atom_lookup(L.pcm)
    # ... other modality atoms have their own canonical-atom tables
    L.id = id
    L.coord = coord
    L.hilbert_index = hilbert

for each tier t in increasing order (T1, T2, T3, ...):
    for each node N at tier t:
        N.id = hash128_merkle(tier=t, [child.id for child in N.children])
        N.coord = math4d_centroid([child.coord for child in N.children])
        N.hilbert_index = hilbert4d_encode(N.coord)

return populated_tier_tree
```

**Output**: Same `TierTree`, every node populated. Caller can now feed it to [`SubstrateCRUD.apply()`](0050-substrate-crud-write-surface.md) for trunk-to-leaf dedup + bulk insert, OR can just read trunk.id to check existence, OR can use the populated tree to build attestations referencing the entity IDs.

### Uses existing engine kernels — no new primitives

- `hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out)` — already in [`engine/core/src/hash128.c`](../../engine/core/src/hash128.c) (48 LOC, real impl, paper-pinned).
- `math4d_centroid(const double* points, size_t n_points, double out[4])` — already in [`engine/core/src/math4d.c`](../../engine/core/src/math4d.c) (175 LOC, real, 31 TESTs / 57 ASSERTs).
- `hilbert4d_encode(const double p[4], hilbert128_t* out)` — already in [`engine/core/src/hilbert4d.c`](../../engine/core/src/hilbert4d.c) (174 LOC, real Skilling 2004 impl, 8 TESTs).
- `codepoint_table_lookup(uint32_t codepoint)` — header declared in [`engine/core/include/laplace/core/codepoint_table.h`](../../engine/core/include/laplace/core/codepoint_table.h); implementation is currently a stub awaiting the perfcache build path landing (see [Chunk 3 / Issue #3](https://github.com/SaltyPatron/Laplace/issues/3) + [UnicodeDecomposer / Issue #183](https://github.com/SaltyPatron/Laplace/issues/183)).

HashComposer is the *composition* of these — it doesn't introduce new math, it sequences existing kernels in the correct leaf-to-trunk order.

### Placement

- **Algorithm implementation in C/C++** under `engine/core/src/hash_composer.{c,cpp}` + header at `engine/core/include/laplace/core/hash_composer.h`. C ABI per [RULES.md R14](../../RULES.md). In `liblaplace_core` per [ADR 0024](0024-engine-modularization.md) so it's loadable by the PG extension (for prompt-ingest path) AND by the C# orchestration layer (for ingestion). No oneMKL dependency — pure scalar compute on small structures (per ADR 0024's core-vs-dynamics split).
- **C ABI shape**: `hash_composer_compose(tier_tree_t* tree)` — in-place population. Opaque `tier_tree_t*` handle shared with TextDecomposer per [RULES.md R22](../../RULES.md).
- **Test coverage**: GoogleTest under `engine/core/tests/test_hash_composer.cpp` per [STANDARDS.md Testing](../../STANDARDS.md). Cross-language consistency test (SQL wrapper + C# binding produce identical populated TierTrees byte-for-byte).
- **C# binding**: `Laplace.Engine.Core` per [ADR 0026](0026-csharp-project-structure.md).

### Performance properties

- **T0 lookups**: ~10ns each (L2/L3 cache hit on perfcache) per [ADR 0006](0006-perfcache-and-db-seed-siblings.md). No DB round-trip.
- **T≥1 hashes**: BLAKE3 over 16-byte child IDs concatenated — one BLAKE3 call per node, runs in 10s–100s of ns depending on child count.
- **T≥1 centroids**: trivial sum-and-divide over 4D doubles, ns per node.
- **T≥1 Hilbert**: Skilling 2004 algorithm, pure integer bit-twiddling, ns per node.
- **Total cost** for a 1000-word text (≈5000 graphemes ≈25,000 codepoints): ~25K perfcache lookups (~250µs cumulative cache-resident) + ~5K Merkle composes + ~5K centroids + ~5K Hilberts ≈ **sub-millisecond total**. Zero DB interactions.

The microsecond inference + microsecond ingest contract per the 2026-05-24 conversation depends on this primitive staying cache-resident + DB-free.

### What HashComposer does NOT do

- Decompose anything ([TextDecomposer / ADR 0047](0047-text-decomposer-pure-primitive.md) and analogous per-modality decomposers do that).
- Touch the database ([SubstrateCRUD / ADR 0050](0050-substrate-crud-write-surface.md) does that).
- Emit attestations (per-source decomposer's job, runs AFTER HashComposer).
- Build the `SubstrateChange` intent ([per ADR 0049](0049-substrate-change-intent-type.md) — the per-source decomposer assembles the intent from the populated TierTree plus its source-specific attestations).
- Know about any source format (decoupled from caller identity).
- Maintain state across calls (pure function — same input always produces same populated output).

## Consequences

- **One canonical content-addressing path** for the whole substrate. Bug fix in HashComposer applies uniformly to every decomposer. Cross-source hash consistency is automatic.
- **All compute is client-side and cache-resident**. The microsecond contract holds because every operation is either L2/L3 cache (T0 perfcache) or in-CPU register operations (Merkle, centroid, Hilbert).
- **Composes cleanly with TextDecomposer + SubstrateCRUD**. The per-source decomposer's ingest path becomes: `strip-markers → TextDecomposer → HashComposer → build attestations → SubstrateCRUD.apply()`. Each stage pure, each composable, each testable independently.
- **Per-modality atom-tables required**. T0 lookups for text route through codepoint_table; per ADR 0040 + ADR 0043 other modalities (Pixel, Audio_Sample, Patch, etc.) have their own canonical-atom tables. HashComposer's `case` on leaf modality is the central dispatch — adding a new modality means adding a new atom-table + a new case here. Documented future work; v0.1 ships with codepoint_table only.
- **Determinism contract anchored on perfcache + engine kernels**. Cross-machine reproducibility requires (a) the perfcache being byte-identical across machines for the same Unicode version per ADR 0006, (b) the engine kernels (hash128_merkle, math4d_centroid, hilbert4d_encode) being FP-deterministic per [RULES.md R7](../../RULES.md) (no `-ffast-math`, deterministic reduction order). HashComposer inherits both invariants.
- **Codepoint_table.c stub is the gate.** HashComposer's T0 lookup path depends on `codepoint_table_lookup()` being real, which depends on the perfcache being built, which depends on UnicodeDecomposer being implemented. That dependency chain (UnicodeDecomposer → perfcache → codepoint_table → HashComposer → every text-bearing decomposer) is the actual Chunk-3 critical path.

## Alternatives considered

- **Inline hash composition in each per-source decomposer.** Rejected — duplication anti-pattern per ADR 0016 + STANDARDS.md. N decomposers, N implementations, N drift surfaces, N bugs.
- **Combine TextDecomposer + HashComposer into one primitive.** Rejected — couples decomposition (trunk-to-leaf, modality-specific) with content-addressing (leaf-to-trunk, modality-agnostic). They're orthogonal concerns with different traversal directions per the 2026-05-24 architecture clarification. Keeping them separate lets per-modality decomposers (image, audio, code) share HashComposer without sharing TextDecomposer.
- **Single-pass interleaved decompose+hash.** Rejected — the two phases have opposite traversal directions. Decomposition is top-down (you can't segment graphemes before identifying the sentence boundary); hashing is bottom-up (you can't hash a parent before its children). Interleaving forces awkward state machines for no win.
- **Hash composition PG-side via a stored procedure.** Rejected — violates [RULES.md R6](../../RULES.md) ("PG stores rows and maintains indices. It does NOT compute hashes, coordinates, Hilbert indices..."). Hashing is engine compute; PG just stores the result.
- **Lazy hash computation (only hash when needed by SubstrateCRUD's existence check).** Rejected — the existence check requires the trunk hash, which requires all child hashes, which requires all grandchild hashes, etc. Lazy evaluation collapses to eager evaluation in practice because the dedup walk needs the full populated tree. Better to do it once up front in a clear leaf-to-trunk sweep.

## References

- [RULES.md R5](../../RULES.md) — attestation idempotency
- [RULES.md R6](../../RULES.md) — DB as dumb columnar store (no PG-side compute)
- [RULES.md R7](../../RULES.md) — determinism by construction
- [RULES.md R14](../../RULES.md) — C ABI at engine boundaries
- [RULES.md R16](../../RULES.md) — separation of concerns (math in C/C++)
- [RULES.md R22](../../RULES.md) — use existing types
- [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md)
- [STANDARDS.md ID discipline](../../STANDARDS.md)
- [STANDARDS.md Cross-language consistency](../../STANDARDS.md)
- [GLOSSARY.md Tiered Merkle DAG](../../GLOSSARY.md)
- [GLOSSARY.md Canonical coordinate](../../GLOSSARY.md)
- [GLOSSARY.md Hilbert index](../../GLOSSARY.md)
- [ADR 0006](0006-perfcache-and-db-seed-siblings.md) — perfcache + DB seed siblings (T0 lookup target)
- [ADR 0015](0015-blake3-for-entity-hashing.md) — BLAKE3-128 (Merkle composition algorithm)
- [ADR 0016](0016-reusable-helpers-discipline.md) — reusable helpers
- [ADR 0024](0024-engine-modularization.md) — engine modularization (placement in `liblaplace_core`)
- [ADR 0026](0026-csharp-project-structure.md) — C# project structure (P/Invoke binding)
- [ADR 0040](0040-multi-modal-entity-types-universal-t0.md) — multi-modal entity types
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap order (Stage 5 perfcache + T0 seed)
- [ADR 0043](0043-composite-decomposer-architecture.md) — composite decomposer (HashComposer is one of the composed primitives)
- [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md) — produces the `TierTree` HashComposer populates
- [ADR 0049 SubstrateChange intent type](0049-substrate-change-intent-type.md) — what callers build from the populated TierTree
- [ADR 0050 SubstrateCRUD write surface](0050-substrate-crud-write-surface.md) — consumes the populated TierTree (via the SubstrateChange intent) for trunk-to-leaf dedup
- [`engine/core/src/hash128.c`](../../engine/core/src/hash128.c) — `hash128_merkle` impl
- [`engine/core/src/math4d.c`](../../engine/core/src/math4d.c) — `math4d_centroid` impl
- [`engine/core/src/hilbert4d.c`](../../engine/core/src/hilbert4d.c) — `hilbert4d_encode` impl
- [`engine/core/include/laplace/core/codepoint_table.h`](../../engine/core/include/laplace/core/codepoint_table.h) — T0 lookup interface (impl currently stub)
- Skilling, John (2004). "Programming the Hilbert curve". AIP Conf. Proc. 707, 381. doi:10.1063/1.1751381.
- Conversation 2026-05-24: three-direction ingest pipeline clarification (trunk→leaf decompose, leaf→trunk hash, trunk→leaf dedup).
