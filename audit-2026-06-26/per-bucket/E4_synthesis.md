## Bucket: E4_synthesis (engine/synthesis — GGUF/model export path)

### Files read (coverage checklist — all 37 read IN FULL)
- [x] engine/synthesis/CMakeLists.txt
- [x] engine/synthesis/include/laplace/synthesis/arch_template.h
- [x] engine/synthesis/include/laplace/synthesis/bf16_decoder.h
- [x] engine/synthesis/include/laplace/synthesis/f32_gather.h
- [x] engine/synthesis/include/laplace/synthesis/feature_extractor.h
- [x] engine/synthesis/include/laplace/synthesis/format_writer.h
- [x] engine/synthesis/include/laplace/synthesis/gguf_writer.h
- [x] engine/synthesis/include/laplace/synthesis/qk_pairs_threshold.h
- [x] engine/synthesis/include/laplace/synthesis/qk_pairs_threshold_pruned.h
- [x] engine/synthesis/include/laplace/synthesis/qk_project_cached.h
- [x] engine/synthesis/include/laplace/synthesis/recipe.h
- [x] engine/synthesis/include/laplace/synthesis/tensor_decompose.h
- [x] engine/synthesis/include/laplace/synthesis/version.h
- [x] engine/synthesis/src/arch_template.cpp
- [x] engine/synthesis/src/bf16_decoder.c
- [x] engine/synthesis/src/f32_gather.c
- [x] engine/synthesis/src/feature_extractor.cpp  — FULL STUB
- [x] engine/synthesis/src/format_writer.cpp
- [x] engine/synthesis/src/gguf_writer.cpp
- [x] engine/synthesis/src/qk_pairs_threshold.cpp
- [x] engine/synthesis/src/qk_pairs_threshold_pruned.cpp
- [x] engine/synthesis/src/qk_project_cached.cpp
- [x] engine/synthesis/src/recipe.cpp
- [x] engine/synthesis/src/tensor_decompose.cpp
- [x] engine/synthesis/src/version.cpp
- [x] engine/synthesis/tests/CMakeLists.txt
- [x] engine/synthesis/tests/test_arch_template.cpp
- [x] engine/synthesis/tests/test_bf16_decoder.cpp
- [x] engine/synthesis/tests/test_feature_extractor.cpp
- [x] engine/synthesis/tests/test_gguf_writer.cpp
- [x] engine/synthesis/tests/test_qk_pairs_threshold.cpp
- [x] engine/synthesis/tests/test_qk_pairs_threshold_pruned.cpp
- [x] engine/synthesis/tests/test_qk_project_cached.cpp
- [x] engine/synthesis/tests/test_recipe.cpp
- [x] engine/synthesis/tests/test_tensor_decompose.cpp
- [x] engine/synthesis/tests/test_version.cpp

Cross-checked against callers: `app/Laplace.Engine.Synthesis/NativeInterop.cs` (P/Invoke binding),
`app/Laplace.Cli/FoundryExport.cs`, `app/Laplace.Cli/FoundryCommands.cs`,
`app/Laplace.Engine.Synthesis.Tests/QkPairsThresholdParityTests.cs`.

### Liveness map (what the real export actually calls)
LIVE (invoked by the foundry export, FoundryExport.cs / FoundryCommands.cs):
`laplace_synthesis_version`, `recipe_parse/get_field/free`, `laplace_bf16_decode`,
`laplace_f32_gather_to_f64`, `tensor_svd_truncate`, `arch_template_load`,
`arch_template_required_tensors` (manifest only), `compute_substrate_gram`
(FoundryExport.cs:1331 `ProjectOperator`), all of `gguf_writer_*` and `format_writer_*`.

NOT invoked anywhere outside tests/binding (verified by repo-wide grep on the C# names):
- `arch_template_materialize_tensor` + its helpers `materialize_token_axis`,
  `materialize_interior_uniform`, `materialize_interior_binary_uniform`, `materialize_norm`
  (bound at NativeInterop.cs:44 but zero call sites).
- `compute_qk_pairs_above_threshold`, `compute_qk_pairs_above_threshold_pruned`,
  `project_qk_layer`, `score_qk_head_cached` — called ONLY from QkPairsThresholdParityTests.cs.
- `feature_extractor_*` — full stub; only test is StubLoadReturnsNull.

So the GGUF writing + recipe + SVD + substrate-gram operator are the live synthesis spine; the
QK-pair attention extractors and the substrate-view tensor materializer are an unwired alternate lane.

---

### Findings

**[F1] feature_extractor.cpp:9-32 — SEVERITY: MEDIUM — CATEGORY: dead-code / MVP-stub**
CLAIM: The entire feature_extractor module is a non-functional stub. `feature_extractor_load`
unconditionally `return nullptr;` (ignores extractor_name), `feature_extractor_extract` returns
`-1`, `feature_extractor_output_dim` returns `0`. The struct is `{ int _placeholder; }`.
VERIFIED: read full source; `feature_extractor_free` does `delete fe` on a pointer that load can
never produce (always null). test_feature_extractor.cpp codifies the stub
(`EXPECT_EQ(fe, nullptr)`). No C# caller exists (grep: only the binding + the stub test).
CONFIDENCE: high. NOTE: this is honest (test names it "Stub"), not faked — but it is shipped dead
surface bound into NativeInterop.cs:161-173 as if usable.

**[F2] test_arch_template.cpp:23-26 vs arch_template.cpp:173-182 — SEVERITY: HIGH — CATEGORY: fake-test / correctness**
CLAIM: Test `LoadUnknownReturnsNull` asserts `arch_template_load("mamba") == nullptr`, but the
implementation returns non-null for ANY non-null name:
```
arch_template_t* arch_template_load(const char* template_name) {
    if (!template_name) return nullptr;
    auto* t = new arch_template(); t->arch_name = template_name; return t;   // "mamba" => non-null
}
```
So this test must FAIL if compiled and run. Two possibilities, both defects: (a) the synthesis
gtest suite is not actually being run/green (the repo culture and MKL gating make this plausible),
or (b) it is a known-red test. Either way the assertion is false against the code.
VERIFIED: traced both definition and the test; `arch_name` is never consulted anywhere
(tensor_manifest does not read it).
CONFIDENCE: high.

**[F3] arch_template.cpp:25-163,173-182 — SEVERITY: MEDIUM — CATEGORY: correctness / invention-violation (silent wrong output)**
CLAIM: `arch_template_load` ignores `template_name` entirely; `tensor_manifest` always emits a
single hardcoded HF-CausalLM/Llama layout regardless of architecture. Feeding a non-Llama recipe
(e.g. BERT/`kMiniLmConfig` in the test, or mamba/MoE-without-the-right-keys) silently yields a Llama
tensor manifest rather than rejecting or dispatching. The test itself demonstrates this: it loads
`"llama"` but parses a BERT config and checks Llama-shaped `k_proj`. There is no architecture
dispatch table despite the API pretending to ("template_name", `arch_template_load("mamba")`).
VERIFIED: read tensor_manifest in full — only recipe numeric fields drive shapes; `arch_name` unused.
CONFIDENCE: high. (For a foundry that only ever exports Llama this is "fine but mislabeled"; the API
surface and the unknown-arch test advertise capability that does not exist.)

**[F4] arch_template.cpp:234-353 — SEVERITY: MEDIUM — CATEGORY: dead-code + invention-concern**
CLAIM: The whole substrate-view tensor materializer is DEAD relative to the live export and, were
it wired, would emit degenerate interior weights. `arch_template_materialize_tensor` is bound
(NativeInterop.cs:44) but has zero call sites; the live FoundryExport builds embed/lm_head via
SVD (`FactorAdjacency`/`FactorSparseRandomized`) and attention operators via `compute_substrate_gram`
+ `Factor`, never this path. The materializer's content, if used: `materialize_interior_uniform`
(lines 249-270) and `materialize_interior_binary_uniform` (272-293) fill EVERY interior matrix
(v_proj, o_proj, all MLP) either with the SAME gram values tiled modulo basis_dim, or — in the
no-gram fallback — with a SINGLE constant `per_cell` in every cell (`for cell ... write_dtype(per_cell)`).
A constant or rank-1-tiled weight matrix is a degenerate transformer layer; every layer would be
identical. `materialize_norm` clamps the norm scalar to [0.5, 2.0]. This is the concrete code behind
the "incoherent/bigram-like export" prior concern — but it is NOT the path the foundry runs, so the
prior concern, as it pertains to THIS engine code, is about dead code.
VERIFIED: read materialize_* in full; grep confirms no caller of ArchTemplateMaterializeTensor.
CONFIDENCE: high (deadness); high (degeneracy of the fill math).

**[F5] arch_template.cpp:219-228 — SEVERITY: LOW — CATEGORY: correctness**
CLAIM: `write_dtype` only distinguishes dtype==0 (f32, memcpy) from "else" (bf16 by `bits>>16`
truncation). It does NOT produce IEEE float16. But `tensor_manifest` sets dtype=1 when
`torch_dtype=="float16"` (lines 77,73). So an f16-typed tensor would be written with bf16 bit
layout, mislabeled in GGUF as F16 (dtype id 1 via dtype_from_api) => corrupt values. Only latent
because the materializer (F4) is dead. CONFIDENCE: high (logic), low (impact, path dead).

**[F6] arch_template.cpp:356-411 + FoundryExport.cs:1331 — SEVERITY: MEDIUM — CATEGORY: correctness / altitude (hard MKL dependency on a live path)**
CLAIM: `compute_substrate_gram` (the LIVE operator-synthesis core) has NO non-MKL fallback: under
`#else` it `return -2;`. Its live caller `ProjectOperator` throws on any nonzero rc
(`compute_substrate_gram rc=-2`). Therefore on any build without MKL/TBB the foundry's
attention-operator export aborts. CMakeLists.txt:49-76 makes MKL OPTIONAL by default
(`LAPLACE_SYNTHESIS_REQUIRE_MKL OFF`, `find_package(MKL ... QUIET)`) and only WARNs, so a
default build links a `laplace_synthesis` whose live gram path is guaranteed to fail at runtime.
Contrast: the qk noise-floor functions and tensor_svd all have meaningful behavior only under MKL
too (svd returns -2 without MKL → every Factor/Basis call in FoundryExport throws). Net: the entire
synthesis library is effectively MKL-required for real work, but the build advertises it as optional.
VERIFIED: read the `#ifdef LAPLACE_HAS_MKL`/`#else return -2` in arch_template.cpp,
tensor_decompose.cpp; read ProjectOperator/Factor/BuildBasis throw-on-rc in FoundryExport.cs.
CONFIDENCE: high.

**[F7] qk_pairs_threshold.cpp / qk_pairs_threshold_pruned.cpp / qk_project_cached.cpp — SEVERITY: MEDIUM — CATEGORY: fork / dead-code**
CLAIM: Three coexisting implementations of the same QK-pair-above-noise-floor extraction. The
plain version recomputes projections; `_pruned` adds Cauchy-Schwarz norm pruning (sort keys by
‖k‖, binary-search a candidate prefix); `project_cached` precomputes Q/K caches across all heads
once then scores per (head,kv_head). They are mutually cross-validated by tests (good), but NONE is
called by FoundryExport/FoundryCommands — only by QkPairsThresholdParityTests.cs. This is precisely
the "converge, don't fork" smell: three lanes for one job, kept alive only by their own parity
tests, while the live export uses the unrelated `compute_substrate_gram`→SVD operator path. Either
this is an abandoned attention-extraction design (delete) or the intended one that was never wired
(wire it and retire compute_substrate_gram). VERIFIED: read all three .cpp in full; repo-wide grep
of the C# entry-point names shows test-only usage.
CONFIDENCE: high (deadness in production), high (functional correctness of the math itself).

**[F8] f32_gather.c:21-33 — SEVERITY: LOW — CATEGORY: correctness (undocumented contract)**
CLAIM: For `row_map[r] < 0` the function `continue`s without writing `out + r*d`, leaving that
output row UNINITIALIZED (not zeroed). The header documents nothing about skip semantics. A caller
expecting zero-fill for unmapped rows gets garbage. CONFIDENCE: high (code), low (impact —
depends on caller; the gather is used in the model-ingest f32→f64 path, not the export bucket core).

**[F9] bf16_decoder.c:6-9 + f32_gather.c:7-10 + CMakeLists.txt:78-84 — SEVERITY: LOW — CATEGORY: perf**
CLAIM: The AVX2 fast paths are gated on `defined(__AVX2__) && defined(__x86_64__)`. MSVC defines
neither `__AVX2__` (uses `/arch:AVX2` + `_M_X64`) nor `__x86_64__`, and CMakeLists only adds
`-mavx2` for Intel/GNU/Clang, never MSVC. So on the Windows/MSVC build (this repo's primary
platform per env) both SIMD kernels silently fall back to scalar. Correctness unaffected (scalar
tail is exact), pure throughput loss on a hot decode/gather path. CONFIDENCE: high.

**[F10] recipe.cpp:41-47,49-64 — SEVERITY: LOW — CATEGORY: correctness (robustness)**
CLAIM: The hand-rolled JSON parser is flat-only and lossy by design: nested objects are stored as
the literal string "<object>" (line 114), arrays keep only the first string element
(parse_json_array_first_string), and `parse_json_primitive` stops at whitespace/`,`/`}`/`]` so a
numeric value is captured as a string token. This is adequate for HF config.json (flat scalars) and
the tests pass, but any field whose value is a nested object or a numeric array is silently dropped
or stringified. No call site currently needs those, so low. CONFIDENCE: high.

**[F11] gguf_writer.cpp:282-313 — SEVERITY: INFO — CATEGORY: correctness (verified GOOD)**
CLAIM/VERIFIED: The GGUF v3 container is written correctly. Magic "GGUF" + version 3 (LE),
tensor_count then metadata_count as u64, KV entries with correct type tags (u32/i32/f32/bool/string/
array), tensor infos (name, u32 n_dims, u64 dims, u32 dtype, u64 offset). Tensor data offsets are
cumulative, each aligned up to 32 and relative to the data-section start; the header is padded to 32
before data, and each tensor's data is padded to 32 — so stored offsets match the on-disk data
layout. Default alignment 32 matches the GGUF default (no `general.alignment` emitted, consistent).
`gguf_dtype_element_size` cannot return 0 in practice because `dtype_from_api` defaults unknown→BF16.
The unused `data_section_start` (lines 294-296) is dead-but-harmless. No bug found.
CONFIDENCE: high.

**[F12] format_writer.cpp:128-207 — SEVERITY: INFO — CATEGORY: correctness (verified GOOD, minor)**
CLAIM/VERIFIED: safetensors writer is correct: 8-byte LE header length, JSON header with
dtype/shape/data_offsets, contiguous tensor blob, plus index.json/config.json/tokenizer.json/
provenance.json. `add_tensor` validates `expected == data_len` (good). Two minor notes:
(a) only "safetensors" format accepted; "gguf" is a separate writer (no fork here, intentional split).
(b) the per-tensor header entry uses a fixed `char entry[1024]` (line 153); a tensor name approaching
~950+ bytes could truncate the JSON via snprintf. HF names are short, so LOW/INFO. CONFIDENCE: high.

**[F13] tensor_decompose.cpp:10-68 — SEVERITY: INFO — CATEGORY: correctness (verified GOOD)**
CLAIM/VERIFIED: `tensor_svd_truncate` validates args, uses LAPACKE_sgesdd 'S', accumulates dropped
energy from smallest singular value upward against `rel_err_tol^2 * total` (Frobenius), forces
rank>=1 unless all-zero (rank 0). U copied with kmax stride (first r cols), S/Vt first r rows
contiguous — correct. Returns -2 without MKL (see F6). Tests cross-check Frobenius reconstruction.
No bug. CONFIDENCE: high.

**[F14] qk_*.cpp (all three) — SEVERITY: INFO — CATEGORY: correctness (verified GOOD)**
CLAIM/VERIFIED: Numerically careful (Neumaier compensated sums everywhere), deterministic across
thread counts and query windows (count→prefix-offset→write three-pass design; whole-row overflow
prefix). Pruning in `_pruned`/`project_cached` is a sound Cauchy-Schwarz bound (|q·k| ≤ ‖q‖‖k‖;
prefix keeps ‖k‖ ≥ floor/‖q‖). Tests assert bitwise parity between all three and a serial
reference, plus determinism and overflow semantics — these are REAL tests, not fakes. Only defect
is that the whole trio is unwired (F7). CONFIDENCE: high.

### Bucket summary
- CRITICAL: 0
- HIGH: 1 (F2 broken/false test)
- MEDIUM: 5 (F1 stub surface, F3 silent arch coercion, F4 dead+degenerate materializer,
  F6 hidden hard-MKL dependency on the live path, F7 three-way QK fork unwired)
- LOW: 4 (F5, F8, F9, F10)
- INFO: 4 (F11–F14 — verified-correct GGUF/safetensors/SVD/QK cores)

Single worst issue: **F6** — `compute_substrate_gram` and `tensor_svd_truncate` (both on the LIVE
foundry export path) have no non-MKL fallback (`return -2`), yet CMake makes MKL optional-by-default
and only warns. A default-configured build links a synthesis library whose real export work throws
at runtime. (F2 is the worst *test* defect: an assertion that contradicts the code, implying the
engine gtest suite is not actually green/run.)

Note on the prior "incoherent/bigram-like export" concern: within THIS bucket the degenerate
constant/tiled interior-weight code (F4) is DEAD; the live embed/lm_head and attention-operator
synthesis lives in C# (FoundryExport.cs FactorAdjacency / ProjectOperator) and is out of bucket —
flag for the app-layer auditor that lm_head is factored from a single adjacency/PPMI plane (literal
bigram structure) at FoundryExport.cs ~1019/1109.

### Disparagement check
No Claude-authored ✅/❌/DEAD/"broken" status tags were found inside these engine source/test files
(they are clean of editorializing). Deadness/correctness above is established from code + call-graph,
not from any in-code status tag.
