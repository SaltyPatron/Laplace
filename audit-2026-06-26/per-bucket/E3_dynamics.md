## Bucket: E3_dynamics (engine/dynamics)

### Files read (coverage proof)
- [x] engine/dynamics/CMakeLists.txt
- [x] engine/dynamics/include/laplace/dynamics/bilinear_edges.h
- [x] engine/dynamics/include/laplace/dynamics/eigenmaps.h
- [x] engine/dynamics/include/laplace/dynamics/ffn_edges.h
- [x] engine/dynamics/include/laplace/dynamics/gram_schmidt.h
- [x] engine/dynamics/include/laplace/dynamics/init.h
- [x] engine/dynamics/include/laplace/dynamics/procrustes.h
- [x] engine/dynamics/src/bilinear_edges.cpp
- [x] engine/dynamics/src/eigenmaps.cpp
- [x] engine/dynamics/src/ffn_edges.cpp
- [x] engine/dynamics/src/gram_schmidt.cpp
- [x] engine/dynamics/src/init.cpp
- [x] engine/dynamics/src/procrustes.cpp
- [x] engine/dynamics/tests/CMakeLists.txt
- [x] engine/dynamics/tests/test_bilinear_edges.cpp
- [x] engine/dynamics/tests/test_eigenmaps.cpp
- [x] engine/dynamics/tests/test_gram_schmidt.cpp
- [x] engine/dynamics/tests/test_init.cpp
- [x] engine/dynamics/tests/test_procrustes.cpp

Cross-checked supporting files (outside bucket, for verification only):
engine/core/include/laplace/core/score.h, engine/core/src/score.c,
app/Laplace.Engine.Dynamics/NativeInterop.cs, app/Laplace.Engine.Dynamics/MklAvailability.cs,
app/Laplace.Decomposers.Model/ModelTokenEdgeETL.cs, app/Laplace.Cli/Program.cs,
app/Laplace.Cli/FoundryExport.cs (caller sites only).

### Verdict up front
The math in this bucket is **real, not stubbed**, and the heavy compute genuinely lives in the
native lib (invariant 5 respected). The transformer-circuit kernels feed the Glicko consensus model
correctly (invariant 4 respected): every circuit scalar is turned into a Glicko game via
`laplace_score_fp` and folded as `games=1` attestations, with accumulation deferred to the DB
consensus fold (verified in ModelTokenEdgeETL.cs lines 328-329, 364-366). Tests are genuine,
property-based, and would catch real regressions. No identity/tier/fork violations. The findings
below are mostly perf/altitude footguns and one dead-in-app variant.

---

### Finding 1 — MKL-gated kernels are hard no-ops (return -2) on a non-MKL build; CMake permits that build
FILE: engine/dynamics/src/bilinear_edges.cpp:58-62, :81-84, :102-105; engine/dynamics/src/ffn_edges.cpp:103-107
SEVERITY: LOW (mitigated) — would be HIGH without the mitigation below
CATEGORY: correctness / invention-violation (conditional silent data drop)
CLAIM: `bilinear_edges_tile`, `project_embedding`, `project_embedding_d`, and `ffn_token_pairs_tile`
have NO compute path without `LAPLACE_HAS_MKL` — the `#else` branch just `(void)`-casts the args and
`return -2`. CMakeLists.txt (lines 61-85) explicitly allows an "Eigen-only fallback" build that emits
only a `message(WARNING)`, not an error, unless `-DLAPLACE_REQUIRE_MKL=ON`. On such a build, ALL
four model-circuit planes (SIMILAR_TO / ATTENDS / OV_RELATES / COMPLETES_TO) produce zero edges; the
C# caller (ModelTokenEdgeETL.RunBilinearTile/RunFfnTile, lines 406-437) maps `rc != 0` to `-1`, the
plane emitters log a warning and `yield break`, and model ingestion reports success having folded
nothing. Note the inconsistency: eigenmaps / gram_schmidt / procrustes DO have working Eigen
implementations with no MKL dependency, so the gating is asymmetric — only the GEMM kernels degrade
to no-ops rather than falling back to a plain (or Eigen) triple-loop.
VERIFIED: Read both `#else` branches (return -2). Traced CMake fallback (WARNING, not FATAL).
Traced caller treatment in ModelTokenEdgeETL.cs:418, 324, 161, 217-218 (warn + skip on nonzero rc).
MITIGATION (verified): app/Laplace.Cli/Program.cs:45-46 calls `MklAvailability.EnsureOrThrow()` at
CLI startup, which invokes `bilinear_edges_tile` and throws if it returns -2 — so the *CLI* fails
fast instead of silently dropping. Caveats keeping this at LOW not zero: (a) the check is bypassable
via `LAPLACE_SKIP_MKL_CHECK=1` (Program.cs:45), which re-enables the silent-drop path; (b) only the
CLI guards — any other host (API, the gtest suite, a direct P/Invoke consumer) has no such guard.
CONFIDENCE: high.

### Finding 2 — C# tile callers ignore the `overflow` out-flag (benign today, latent truncation)
FILE: app/Laplace.Decomposers.Model/ModelTokenEdgeETL.cs:415-419 (RunFfnTile), :431-434 (RunBilinearTile)
SEVERITY: LOW
CATEGORY: correctness (latent)
CLAIM: Both kernels set `*overflow=1` and return early (rc still 0) when `cnt >= cap`
(bilinear_edges.cpp:47, ffn_edges.cpp:92). The C# wrappers read `&overflow` but never inspect it —
they return `(int)count` on rc==0 regardless. If overflow ever fired, the caller would silently fold
only the first `cap` edges and drop the rest with no signal. Today this cannot trigger because
`cap = RowTile * n` (lines 314, 350) exactly equals the maximum pairs a tile can emit
(`t*n_right` with `t<=RowTile`, `n_right=n`), so the buffer is precisely sized. It becomes a silent
data-loss bug the moment cap sizing, RowTile, or the column count diverges from that invariant.
VERIFIED: cap arithmetic at ModelTokenEdgeETL.cs:314 and :350; overflow semantics at
bilinear_edges.cpp:47 and ffn_edges.cpp:92; wrapper return at :418 and :434 (overflow unread).
CONFIDENCE: high.

### Finding 3 — Dense kNN `laplacian_eigenmaps` is dead in the app (test-only)
FILE: engine/dynamics/src/eigenmaps.cpp:165-220 (entry `laplacian_eigenmaps`)
SEVERITY: INFO
CATEGORY: dead-code
CLAIM: The dense, build-the-kNN-graph-internally variant `laplacian_eigenmaps` has no app caller.
Grep of all *.cs shows only `LaplacianEigenmapsFromSparseGraph` is used (FoundryExport.cs:1176); the
dense `LaplacianEigenmaps` appears solely in the NativeInterop declaration and the gtest. It is
exercised by tests (RecoversRingManifold, DeterministicOnIdenticalInput) but not wired into any
pipeline. Not harmful — flagging as unused surface, not a defect.
VERIFIED: Grep `LaplacianEigenmaps\b` vs `LaplacianEigenmapsFromSparseGraph` across *.cs.
CONFIDENCE: high.

### Finding 4 — No direct unit test for `expand_kv_heads_d` or `norm_rows_d`
FILE: engine/dynamics/src/bilinear_edges.cpp:108-154 (norm_rows_d, expand_kv_heads_d)
SEVERITY: LOW
CATEGORY: fake-test (coverage gap, not a fake test)
CLAIM: `expand_kv_heads_d` (GQA head expansion, used in the ATTENDS and OV planes,
ModelTokenEdgeETL.cs:215, 267) and `norm_rows_d` have no dedicated test in
tests/CMakeLists.txt's set (test_init/procrustes/eigenmaps/gram_schmidt/bilinear only). The GQA index
math `kh = min(n_kv-1, h * n_kv / max(1,n_heads))` is correct by inspection (equals `h / (n_heads/n_kv)`,
the standard grouped-query mapping), and norm_rows_d is exercised indirectly via the C# NormRows path,
but neither has a regression guard. The `kv_dim == attn_dim` fast-path memcpy (line 141-143) is also
untested.
VERIFIED: Read function bodies; confirmed absence from tests/CMakeLists.txt; traced GQA call sites.
CONFIDENCE: high.

### Finding 5 — Tests are REAL and strong (positive finding)
FILE: engine/dynamics/tests/test_eigenmaps.cpp, test_procrustes.cpp, test_gram_schmidt.cpp, test_bilinear_edges.cpp
SEVERITY: INFO
CATEGORY: other (positive)
CLAIM: Contrary to the common "tests assert nothing" failure mode, these are genuine property tests:
eigenmaps recovers a ring manifold and verifies degree-weighted zero-mean + D-orthonormality of the
embedding columns against an analytic path-graph (test_eigenmaps.cpp:91-137); procrustes recovers a
known random rotation+translation to 1e-9, recovers a known scale, bounds the rectangular-projection
residual, and asserts a *noise floor* (NoiseGivesNonzeroResidual, :210-235) so a degenerate
identity-return would fail; gram_schmidt verifies orthonormality, rank-deficiency detection (-4),
and row-span preservation (:106-134); bilinear verifies the tiled result equals a single pass and
matches an independent reference dot-product (:61-92, :10-15). These would catch real math
regressions. `test_init` is trivial (version string) but that matches a trivial unit.
VERIFIED: Read every test body.
CONFIDENCE: high.

### Finding 6 — Procrustes scale formula is correct for the rectangular case (verified, not a bug)
FILE: engine/dynamics/src/procrustes.cpp:43-47
SEVERITY: INFO
CATEGORY: correctness (verified clean)
CLAIM: `scale = singularValues().sum() / PcR.squaredNorm()`. This looks non-standard (textbook
square-orthogonal Procrustes uses `trace(S)/||Pc||_F^2`), but it is the correct least-squares scale
for the model `s·(Pc·R)`: optimal `s = <PcR,Qc>/||PcR||^2`, and `<PcR,Qc> = trace(R^T H) = trace(S)`
(sum of singular values) since `H=U S V^T`, `R=U V^T`. For square orthogonal R, `||PcR||^2=||Pc||^2`
so it reduces to the textbook form; for the thin/rectangular R (source_dim≠4) it is *more* correct.
The rectangular recovery test (test_procrustes.cpp:117-149) confirms. No defect.
VERIFIED: Derived the optimum; matched to code; corroborated by passing rectangular test.
CONFIDENCE: high.

### Finding 7 — Glicko/consensus alignment is correct (invariant 4, verified clean)
FILE: engine/dynamics/src/bilinear_edges.cpp:51, engine/dynamics/src/ffn_edges.cpp:96; engine/core/src/score.c:7-10
SEVERITY: INFO
CATEGORY: invention (verified clean)
CLAIM: Each kernel emits `out_scores[cnt] = laplace_score_fp(v, 1.0)`, the fixed-point ×1e9 game
score `0.5*(1 + v/(1+|v|))`. The caller folds one aggregated attestation per circuit pair
(`games=1`, `sumScoreFp=oS[e]`) and explicitly defers accumulation/sparsity to the DB consensus fold
(ModelTokenEdgeETL.cs:24-26, 342-343, 364-366) — no pre-floor, no top-k fold cap (theta default 0.0,
:37-40; TopPairCollector is decoder-ring sampling only, never affects the fold, :464-466). This
matches invariant 4 (meaning = Glicko fold over attestations, online, no separate drain). The kernels
themselves carry no source/position/order into ids — they emit row/col indices the caller maps to
content-addressed entity ids. No identity (inv 1) or tier (inv 3) violation in this layer.
VERIFIED: Read score_fp; traced emit→fold path; confirmed no floor/cap on the fold.
CONFIDENCE: high.

### Notes on prose/comments
CMakeLists.txt comments ("OK for local dev scaffolding but Production / Epic D requires MKL") and the
ModelTokenEdgeETL header are accurate descriptions of behavior, not the Claude-authored disparagement
the charter warns about — no `DEAD/broken/noisy` tags found in this bucket. The eigenmaps.cpp comment
"SVD preserves inner products; Laplacian eigenmaps would distort them" lives in the *caller*, not
this bucket, and is a correct design justification.

### Bucket summary
- CRITICAL: 0
- HIGH: 0
- MEDIUM: 0
- LOW: 3 (Findings 1, 2, 4)
- INFO: 4 (Findings 3, 5, 6, 7)

Worst issue: **Finding 1** — the four GEMM circuit kernels are no-ops without MKL and CMake permits a
non-MKL build, so on such a build all model-edge ingestion silently folds nothing. It is held to LOW
because the CLI guards it with `MklAvailability.EnsureOrThrow()` (fail-fast), but the guard is
bypassable (`LAPLACE_SKIP_MKL_CHECK=1`) and not shared by non-CLI hosts; an Eigen/plain fallback for
these kernels (as the other three kernels already have) would remove the footgun entirely.

Overall: this bucket is one of the healthy parts of the codebase — real native math, real tests,
correct Glicko wiring, correct altitude. No stubs, no fakes, no fork lanes.
