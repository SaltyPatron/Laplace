# Bucket: E1 — engine/core (native C/C++ core)

### Files read (coverage — all 64)
Headers (32):
- [x] include/laplace/core/astar.h
- [x] include/laplace/core/attestation_engine.h
- [x] include/laplace/core/codepoint_table.h
- [x] include/laplace/core/content_witness_batch.h
- [x] include/laplace/core/glicko2.h
- [x] include/laplace/core/grammar_compose.h
- [x] include/laplace/core/grammar_decomposer.h
- [x] include/laplace/core/grammar_registry.h
- [x] include/laplace/core/grammar_tags.h
- [x] include/laplace/core/grapheme_break.h
- [x] include/laplace/core/grapheme_floor.h
- [x] include/laplace/core/hash128.h
- [x] include/laplace/core/hash_composer.h
- [x] include/laplace/core/hilbert4d.h
- [x] include/laplace/core/intent_stage.h
- [x] include/laplace/core/mantissa.h
- [x] include/laplace/core/math4d.h
- [x] include/laplace/core/merkle_dedup.h
- [x] include/laplace/core/normalize_nfc.h
- [x] include/laplace/core/perfcache_format.h
- [x] include/laplace/core/pos_law.h
- [x] include/laplace/core/relation_law.h
- [x] include/laplace/core/score.h
- [x] include/laplace/core/sentence_break.h
- [x] include/laplace/core/super_fibonacci.h
- [x] include/laplace/core/text_decomposer.h
- [x] include/laplace/core/tier_tree.h
- [x] include/laplace/core/trajectory.h
- [x] include/laplace/core/ucd_property_values.h
- [x] include/laplace/core/unicode_seed.h
- [x] include/laplace/core/version.h
- [x] include/laplace/core/word_break.h

Sources (32):
- [x] src/astar.cpp
- [x] src/attestation_engine.c
- [x] src/codepoint_table.c
- [x] src/content_witness_batch.c
- [x] src/generated/.attestation-law-stamp  (data stamp: `639178733536632972:639173369716741279:639173369716928835` — not code)
- [x] src/generated/pos_law.c
- [x] src/generated/relation_law.c
- [x] src/glicko2.c
- [x] src/grammar_compose.cpp
- [x] src/grammar_decomposer.c
- [x] src/grammar_registry.c
- [x] src/grammar_rows.c
- [x] src/grammar_tags.c
- [x] src/grapheme_break.c
- [x] src/grapheme_floor.c
- [x] src/hash128.c
- [x] src/hash_composer.c
- [x] src/hilbert4d.c
- [x] src/intent_stage.c
- [x] src/mantissa.c
- [x] src/math4d.c
- [x] src/merkle_dedup.c
- [x] src/normalize_nfc.c
- [x] src/score.c
- [x] src/sentence_break.c
- [x] src/super_fibonacci.c
- [x] src/text_decomposer.c
- [x] src/tier_tree.c
- [x] src/trajectory.c
- [x] src/unicode_seed.cpp
- [x] src/version.c
- [x] src/word_break.c

## Invariants — verified PASS (state explicitly, since "engine holds all logic" lives here)
The core genuinely holds the heavy logic; it is not stubbed or delegated. Confirmed against code:

- **Identity = blake3(content), no provenance/tier in id.** `hash128_merkle` (hash128.c:16) hashes `0x01 || child_ids` and explicitly `(void)tier;` — tier is NOT in the id. Codepoint id = `blake3(utf8-of-cp)` (unicode_seed.cpp:267). Physicality id excludes source (`grammar_compose.cpp:607 (void)source_id`; physicality_id_compute hashes only entity_id+type+coord+trajectory). VERIFIED both definition + call sites.
- **Attestation id legitimately includes source/context** (attestation_engine.c:80-104) — correct: an attestation is the provenance record; source is its dedup key, not an entity id. Not a violation.
- **Tier = max(child)+1 emergent** in grammar compose (grammar_compose.cpp:404 `tier = max_tier+1`). VERIFIED.
- **Trajectory packing is lossless.** mantissa.c packs a 128-bit id into 4 doubles as 53/53/53/53-bit mantissa+sign slots (X=53 of lo, Y=11 of lo + 42 of hi, Z=22 of hi + 31 flag bits, M=ordinal/run/21 flag bits) = full 128 id bits + 52 flag bits; `laplace_slot_to_fp`/`laplace_fp_to_slot` round-trip exactly via memcpy of a normalized double. VERIFIED bit-arithmetic.
- **Glicko-2 fixed-point ×1e9, eff_mu = rating − 2·rd.** `laplace_effective_mu_fp` (glicko2.c:433) returns `rating - 2*rd`; SCALE=1e9; `__int128` intermediate mul/div with rounding; Illinois volatility solver present and standard. VERIFIED.
- **Inline fold, not a drain.** `glicko2_fold_uniform_period` (glicko2.c:346) folds N uniform games in one closed-form pass — no separate catch-up drain. VERIFIED.
- **Dedup-is-the-hash, top-down trunk short-circuit.** merkle_dedup.c:32 `merkle_dedup_trunk_shortcircuit` marks `skip[i] = self_existing || parent_skipped` walking child-before-parent order, so a present trunk prunes its whole subtree (O(tier), not O(rows)). content_witness per-batch witness set + DB anti-join replaces the deleted global bank (content_witness_batch.c:209-219). VERIFIED.
- **NFC canonicalization chokepoint** before id computation (text_decomposer.c:26-44) — makes "same content = same hash" structural. normalize_nfc.c is a real full NFD→reorder→NFC impl (Hangul + UCD decomposition + ccc insertion-sort + canonical compose). The max-ccc blocking variant is **correct** (max(intervening ccc) ≥ ccc(C) ⟺ ∃ intervening with ccc ≥ ccc(C)). VERIFIED.

---

## Findings

### F1 — Generic compose grapheme-explodes EVERY non-JSON modality (chess-bug analogue)
- **FILE:LINE:** engine/core/src/grammar_compose.cpp:624-657, 405-439
- **SEVERITY:** HIGH
- **CATEGORY:** invention-violation
- **CLAIM:** `laplace_grammar_compose` has exactly one modality branch: `is_json_modality(modality_id)` (line 72 only matches `"json"`). For every other modality it (1) builds a grapheme floor over the *entire raw input* and pushes all graphemes as tier-1 entities (lines 627-657), and (2) in the per-AST-node leaf path maps each leaf's byte span to graphemes via `laplace_grapheme_floor_span_to_graphemes` (lines 413-438). For a non-text modality whose surface is not prose (chess `"pgn"` is registered in grammar_registry.c:90), this routes the domain surface string through the UAX-29 text grapheme path and explodes ~N chars into N grapheme nodes per record — exactly the O(rows) category error the charter §7 / `chess-substrate-design` memory names ("a board isn't prose").
- **VERIFIED:** traced the only branch point (`is_json_modality`), the unconditional grapheme-floor build for `!json_mod` (line 627), and the `else` leaf-to-grapheme path (line 413). There is no per-modality leaf handling besides JSON.
- **CONFIDENCE:** medium — the code path unambiguously does this; whether the chess engine actually composes through `laplace_grammar_compose` vs a bespoke composer lives outside this bucket (engine/dynamics|synthesis). If chess uses this path, this is the live root cause; if not, code modality (python/rust) still gets grapheme-floor which is defensible for text-shaped source.

### F2 — Convergence anchors use the invented `substrate/.../v1` namespace (the exact §6 pattern)
- **FILE:LINE:** engine/core/src/generated/relation_law.c:197-204 (`substrate/type/<REL>/v1`); generated/pos_law.c:145-156 (`substrate/pos/<UPOS>/v1`); grammar_compose.cpp:21-29 (`substrate/type/grammar/<mod>/<type>/v1`); content_witness_batch.c:22-32 (`substrate/type/{Codepoint,Grapheme,Word,Sentence,Document}/v1`)
- **SEVERITY:** MEDIUM
- **CATEGORY:** invention-violation
- **CLAIM:** Charter §6 mandates concepts/POS/relation-types anchor on the *bare* external inventory id (UPOS `NOUN`, GWN/ConceptNet `IS_A`), "never an invented `substrate/type/X/v1` namespace." The code wraps the inventory name in precisely that namespace: relation type_id = `blake3("substrate/type/IS_A/v1")`, POS = `blake3("substrate/pos/NOUN/v1")`.
- **MITIGATION (verified):** convergence *within* the system still works — all sources route POS through `laplace_pos_resolve_entity` (WordNet "n", Wiktionary "noun", FrameNet "n" all canonicalize to UPOS → one id) and relations through `laplace_relation_resolve_surface` (aliases HAS_HYPERNYM→IS_A etc.). So cross-source convergence on these axes holds. The defect is (a) the literal namespace the charter calls out, and (b) it will NOT converge with any component that anchors on the raw external id, and ILI/ISO-639 anchoring (the real registries) is not done here. Note the disparity also lives in the prose-vs-code: the header/comment claims source-free convergence while the anchor string is an invented constant.
- **VERIFIED:** `type_id_from_canonical` (relation_law.c:197), `laplace_pos_resolve_entity` (pos_law.c:134), and the canonical→UPOS maps (pos_law.c:56-132).
- **CONFIDENCE:** high that the pattern exists; medium on severity given the internal-consistency mitigation.

### F3 — Text content nodes derive type_id purely from tier (kind coupled to depth)
- **FILE:LINE:** engine/core/src/content_witness_batch.c:22-32 (`tier_type_id`)
- **SEVERITY:** LOW
- **CATEGORY:** invention-violation (tension)
- **CLAIM:** For the content/text path, `type_id = f(tier)` (0→Codepoint…4→Document). Charter §3: "KIND lives in type_id, NOT tier." Here the kind label is a pure function of depth, so type carries no independent kind signal for text entities. This is the inverse of the flagged `EntityTier.Vocabulary=5` coupling.
- **MITIGATION:** CLAUDE.md §1 explicitly blesses the codepoint→grapheme→word→sentence→document ladder as "just the text grammar." The grammar-compose path (grammar_compose.cpp:678 `node_type_entity_id`) does NOT couple — it uses the real tree-sitter node-type as kind. So the coupling is text-only and consistent with the documented ladder.
- **VERIFIED:** `tier_type_id` switch on tier; contrast with grammar path's `node_type_entity_id`.
- **CONFIDENCE:** high (fact); the "is it a defect" is design-blessed for text.

### F4 — A* heuristic is hardcoded 0 (degenerates to Dijkstra) and `k_paths` is ignored
- **FILE:LINE:** engine/core/src/astar.cpp:51 `(void)k_paths;`, :97 `double h = 0.0;`, :119
- **SEVERITY:** LOW
- **CATEGORY:** perf / dead-feature
- **CLAIM:** `astar_open` never computes a heuristic (h≡0), so it is uninformed Dijkstra despite the name and the available S³ `coord` geometry that could supply an admissible heuristic. `k_paths` is accepted but discarded — only a single path is ever returned. On `expand` error (n<0) it silently returns an empty query (swallowed error).
- **VERIFIED:** read full file; no h computation, no k-path bookkeeping.
- **CONFIDENCE:** high.

### F5 — Dead code: `update_state` / `__keep_update_state_alive` in grapheme_break.c (+ GCC-only attribute)
- **FILE:LINE:** engine/core/src/grapheme_break.c:53-77, 192-196
- **SEVERITY:** LOW
- **CATEGORY:** dead-code
- **CLAIM:** `update_state` is only referenced by `__keep_update_state_alive`, which is itself `__attribute__((unused))` and uncalled. The live break loop uses inline state updates instead. Both functions are dead. `__attribute__` is GCC/Clang-only.
- **VERIFIED:** grep of references within file; only the keep-alive shim calls it.
- **CONFIDENCE:** high.

### F6 — grammar_compose main emit loop is O(n²) per record
- **FILE:LINE:** engine/core/src/grammar_compose.cpp:667-803
- **SEVERITY:** LOW
- **CATEGORY:** perf
- **CLAIM:** The emit loop rescans all `n` AST nodes to recount/collect children for each node (lines 687-692 and 700-708), and the PRECEDES construction (770-803) is a triple-nested scan (per-parent × per-node × linear search over `precedes_count`). `compose_ast_nodes` already built `children_of` in O(n) but freed it; the emit pass rebuilds via full scans → O(n²) (precedes worse). Bounded because records are streamed small, but a large document/sentence AST will feel it.
- **VERIFIED:** read both loops.
- **CONFIDENCE:** high (complexity); impact depends on per-record AST size.

### F7 — unicode_seed shells out via popen with the input path interpolated unescaped
- **FILE:LINE:** engine/core/src/unicode_seed.cpp:217-223
- **SEVERITY:** LOW
- **CATEGORY:** correctness / other (injection)
- **CLAIM:** For `.zip` inputs it builds `tar -xOf "<path>"` / `unzip -p '<path>'` and `popen`s it; a path containing quotes/`$`/backticks is a shell-injection vector. This is a build-time seed tool fed a trusted local UCD path, so risk is low, but the path is unsanitized.
- **VERIFIED:** read the popen construction.
- **CONFIDENCE:** high (the unescaped interpolation); low real-world exposure.

### F8 — Toolchain assumption: core requires GCC/Clang/clang-cl, not MSVC cl.exe
- **FILE:LINE:** glicko2.c:18 (`__int128`), grapheme_break.c:192 (`__attribute__`), grammar_decomposer.c:99 (`_Thread_local`), relation_law.c:216 (`__atomic_*`)
- **SEVERITY:** INFO
- **CATEGORY:** other (portability)
- **CLAIM:** `__int128` and `__attribute__`/`__atomic_*` are not supported by MSVC; codepoint_table.c/attestation_engine.c carry `_WIN32` branches, so Windows builds exist — they must use clang-cl/MinGW, not cl.exe. Not a bug if that is the intended toolchain; flagged so a future MSVC attempt isn't a surprise.
- **VERIFIED:** the constructs are present; both `_MSC_VER` (TLS) and POSIX paths exist, but the int128/atomics have no MSVC fallback.
- **CONFIDENCE:** medium.

### F9 — perfcache header size checks can integer-overflow on a malformed header
- **FILE:LINE:** engine/core/src/codepoint_table.c:116-119
- **SEVERITY:** LOW
- **CATEGORY:** correctness
- **CLAIM:** `h->records_offset + h->record_count * h->record_size > body_end` (and the decomp/compose variants) multiply/add uint64 fields from the file without overflow guards. `record_count`/`record_size` are validated against constants first (lines 111-112), but `decomp_*`/`compose_*` counts are not, so a crafted header could wrap the product and pass the bound, then the mmap'd pointers index OOB. The file is internally generated (BLAKE3 trailer verified at line 124), so exposure is low.
- **VERIFIED:** read load path; only records_count/size are pre-validated.
- **CONFIDENCE:** medium.

### F10 — Single-child composition aliases parent id to child id (id/tier transparency)
- **FILE:LINE:** engine/core/src/hash_composer.c:24-25 (`if (n==1) *out_id = child_ids[0];`)
- **SEVERITY:** INFO
- **CATEGORY:** correctness (by-design note)
- **CLAIM:** A composite with a single constituent gets the child's exact id while claiming tier+1. This is intentional (a 1-element composition adds no content) and both emit paths handle it safely: content_witness `collapse_idx`/`should_emit_compositional` (content_witness_batch.c:66-94) drop the redundant wrapper, and grammar_compose dedups by id (`compose_id_push`) emitting child-first in reverse so the same-id parent is skipped. No duplicate-id-different-tier row is produced. Documented here only because it makes "tier" non-injective w.r.t. id and could surprise a future caller that assumes id⇒unique tier.
- **VERIFIED:** traced collapse + compose_id_push dedup ordering.
- **CONFIDENCE:** high.

### F11 — Disparagement / stale-prose check
No `DEAD`/`broken`/`degraded`/`semantically random` status tags found in this bucket's code or comments. Comments are mostly accurate to the code (e.g. content_witness_batch.c:209-219 honestly documents the deleted global bank; grammar_compose.h tree-tree contract matches build_containment_tree). One prose/code tension noted in F2 (source-free-convergence comments alongside an invented-namespace anchor). No editorializing to strip.

---

### Bucket summary
- CRITICAL: 0
- HIGH: 1  (F1 — generic compose grapheme-explodes all non-JSON modalities; the documented chess category-error, if chess routes through this path)
- MEDIUM: 1 (F2 — convergence anchors use the invented `substrate/.../v1` namespace §6 forbids; mitigated by internal single-resolver consistency)
- LOW: 6  (F3, F4, F5, F6, F7, F9)
- INFO: 3 (F8, F10, F11)

**Single worst issue:** F1 — the only modality dispatch in `laplace_grammar_compose` is JSON-vs-everything-else, and "everything else" forces the raw surface through the UAX-29 grapheme floor. For any non-prose modality (chess `pgn`) this is the O(rows) category error the charter and the chess memories call the root cause. Everything else in the core is correct and genuinely heavy-lifting (hash/merkle/tier/trajectory/glicko/geometry all live and verified here); this is the one structural invention-violation.
