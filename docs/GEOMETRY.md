# Geometry

The structural engine: constructed position, identity-bearing sequence, and the instrument layer built on both. Status: instrument-tier by ruling (truth is relational; see EPISTEMOLOGY.md) — but the machinery is exact, indexed, and already productive.

## The frame

Space is 4D. Surface entities live on the unit 3-sphere S³; composed interiors occupy the 4-ball (radius_origin < 1 measures interior depth). The frame is **anchored, not learned**:

- **T0 lattice (the law):** all 1,114,112 codepoints are placed by super-Fibonacci spirals — deterministic, uniform, byte-identical across every consumer (the engine's 1-ULP determinism war exists because emitter and runtime must agree exactly). T0 never moves. It is the coordinate system's skeleton.
- **Composition placement:** higher-tier entities derive position from constituents. Empirical law discovered 2026-06-07: **position encodes the constituent multiset** — perfect anagrams collide at geodesic 0 (whale ≡ wheal ≡ waleh ≡ elhwa ≡ welah). Order is deliberately NOT in the point.
- **Witness placement (fireflies):** deposed models' embeddings are projected into the frame by LE+GSO+PA (below), one specimen per (entity × witness) — the S3 morph's PROJECTION rows in physicalities. (Circuit/per-tensor-role granularity was purged with the cell archive, 2026-06-11.)

## Two-channel identity (the discovered law)

An entity's geometric identity has two orthogonal carriers:

| channel | carrier | encodes | comparator |
|---|---|---|---|
| POINT | `coord` (PointZM) | what it is made of (multiset) | geodesic / chord / Hilbert equality |
| CURVE | realized trajectory | how it is arranged (sequence) | Fréchet / Hausdorff |

Consequences, all verified live:
- **Anagram retrieval is a B-tree equality.** Identical position ⇒ identical `hilbert_index` ⇒ `anagrams_of()` joins on the Hilbert key: 31 ms vs 27 s for the spatial formulation. The locality key doubles as a multiset-signature index. Nobody designed this; honest geometry donated it.
- **Fréchet separates what position cannot:** whale~wheal share a point but not a curve.
- **Morphological distance is exact and cheap:** realized word-curves (constituents joined to live T0 coords, `ST_MakeLine` by ordinal) compared by discrete Fréchet at ~3.6 ms/pair on the slow tier. whale~while 0.1149; whale~whole 0.6638; whale~ship 1.7156; whale~Ahab 1.9873.

## The trajectory law (why identity, not coordinates)

Stored trajectories are mantissa-packed: XYZ mantissas carry each constituent's 128-bit id; M packs ordinal, run-length, and flags (T0 constituents self-describe inline via a 21-bit codepoint — `vertex_atom` — skipping even the entity join). Coordinates-as-payload was considered and **rejected** (ruling on record):

1. Reconstruction demands exactness; identity is cryptographic, coordinate-matching is a fragile spatial join — and the inverse (coord→id) exists only at T0.
2. **Positions move by design** (witness placements re-adjudicate; composition inputs can shift). Identity is the only placement-proof cargo; coordinate payloads would rot silently.
3. The index that matters for sequences is prefix-match (trie-shaped; the planned SP-GiST opclass), not bounding boxes — packed IDs are exactly its input.

Realized curves are always built on demand from LIVE coordinates, so curve math measures current geometry, never a snapshot. (Convenience wrapper: `word_curve()`; generalized `realized_trajectory(entity)` is sanctioned future work.)

## Metrics & operators

- `laplace_angular_distance_4d` — geodesic on S³ (acos of normalized dot): THE structural metric. Chord (Euclidean) is monotone with angle on the sphere, so PostGIS ND-GIST KNN (`<<->>`) yields exact angular ranking with index support. (Planner caveat 2026-06-07: type-filtered KNN can fall off the index path — tuning item in OPEN-PROBLEMS.)
- `laplace_frechet_4d` / `laplace_hausdorff_4d` — exact curve/cloud distances (Eiter–Mannila DP; symmetric Hausdorff). Curve↔morphology; cloud↔whole-witness signatures.
- `laplace_dwithin_4d` (squared-distance, sqrt-free), `laplace_distance_4d`, `laplace_radius_origin`.
- `laplace_centroid_4d` — Euclidean vertex mean (interior use). For S³ averaging the engine provides the real thing: `math4d_karcher_mean` with `log_s3`/`exp_s3` tangent maps — weighted iterative Riemannian mean, tested; needed only if the optional consensus-geometry view is ever built.
- `hilbert_index` — Skilling 4D curve, pure integer: locality range-scans on a vanilla btree; equality = multiset identity.
- Full stock PostGIS applies (the refuse-custom-types dividend): GIST, ST_DumpPoints, spatial joins, and GIS tooling (QGIS renders the jar with zero custom viewers).

## LE+GSO+PA: the same instruments, both directions

Pipeline (dynamics lib): **L**aplacian **E**igenmaps (dense points or sparse relation graph) → **G**ram-**S**chmidt **o**rthonormalization → **P**rocrustes **A**lignment, residual recorded.

**Deposition direction (the S3 morph):** reduce a witness's native embedding space and align it onto the substrate frame — one specimen placement per witness (`alignment_residual`, `source_dim` kept).

**Export direction (the foundry — the inversion):** LE runs over the CONSENSUS token→token graph (planes + content-trajectory pairs) to GENERATE a brand-new basis at the mold's dimension; GSO orthonormalizes it; Procrustes anchors it to token content coordinates. The generated spaces owe nothing to any witness's geometry — see SYNTHESIS.md.

The alignment is well-posed **because of the core invention**: content addressing supplies exact point correspondences (the model's "king" IS the substrate's king — the correspondence problem that cripples manifold alignment elsewhere is solved by construction), and the T0 lattice plus already-placed entities supply the fixed target frame. Cross-model geometry is commensurable because identity is content.

## Fireflies (the instrument)

One specimen per witness per entity: SPECIES of the same identity — Llama's king-light and Qwen's king-light in one jar. The species, never a blend, are the product. Ruling: consensus-folding positions is optional derived-view territory; collapsing species destroys the comparative signal that IS the instrument.

Audit capabilities (each a query, not a research program):
- per-entity cross-model belief distance (geodesics);
- whole-cloud signatures → lineage/distillation forensics (Hausdorff between clouds);
- checkpoint-drift diffs (what fine-tuning moved, by name);
- bias measurement in defensible geodesics;
- **Voronoi territories**: tessellate placements → membership-by-geometry, boundary proximity = ambiguity/confusability, empty cells = visible lexical gaps, cross-model carve-up comparison, and geometric cross-validation of relational taxonomy (engine disagreement = audit flag);
- stock GIS rendering throughout.

## The dual engine & frayed edges

Two exact, indexed, orthogonal similarity systems over the same identities:
- **Relational:** ORDER BY eff_mu over witnessed arenas — what testimony binds.
- **Structural:** position/curve mathematics — what form resembles.

Canonical inversion (measured): whale~while structurally near (0.1149) with zero relational testimony; whale~ship relationally bound (μ 2010.5, 116 witnesses) and structurally far (1.7156). A single embedding similarity cannot represent both facts simultaneously; this is the architecture's standing rebuke of cosine-as-meaning.

Their disagreement is generative: structurally-near + relationally-silent pairs are **hypothesis candidates** (frayed edges — the system's own reading list); relationally-strong + structurally-far pairs mark *learned* association as opposed to formal/compositional kinship.

## Geometry status ledger

Working today: T0 law, composition multiset positions, Hilbert keys + anagram equality, realized curves + Fréchet/Hausdorff, angular metric, per-source placements with residuals, the 23_structural_surface functions.
Open (OPEN-PROBLEMS): KNN planner tuning under filters; SP-GiST trajectory prefix opclass; physicalities consolidation implementation (circuit sources, per-role types, retire 19_*); optional Karcher consensus view; batched/SIMD geometry entry points (slow-tier per-row fmgr calls today).
