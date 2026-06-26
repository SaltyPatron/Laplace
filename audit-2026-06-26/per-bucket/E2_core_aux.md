## Bucket: E2_core_aux (engine core tests, tools, grammars, manifest, CMake)

### Files read (coverage proof)
- [x] engine/CMakeLists.txt — read in full
- [x] engine/core/CMakeLists.txt — read in full
- [x] engine/core/grammars/CMakeLists.txt — read in full
- [x] engine/core/grammars/generated/pgn/grammar.js — read in full (hand-authored)
- [x] engine/core/grammars/generated/pgn/parser.c — read in full (1374 ln, generated)
- [x] engine/core/grammars/generated/pgn/tree-sitter.json — read in full
- [x] engine/core/grammars/generated/pgn/tree_sitter/alloc.h — read in full (vendored TS runtime)
- [x] engine/core/grammars/generated/pgn/tree_sitter/array.h — read in full (vendored)
- [x] engine/core/grammars/generated/pgn/tree_sitter/parser.h — read in full (vendored)
- [x] engine/core/grammars/generated/sql/parser.c — VERIFIED generated (1,403,658 ln state table; head+markers checked, standard tree-sitter LANGUAGE_VERSION 15 output — not read line-by-line, disclosed below)
- [x] engine/core/grammars/generated/sql/scanner.c — read in full (vendored tree-sitter-sql)
- [x] engine/core/grammars/generated/sql/tree_sitter/alloc.h — vendored, identical to pgn copy (verified)
- [x] engine/core/grammars/generated/sql/tree_sitter/array.h — vendored (verified)
- [x] engine/core/grammars/generated/sql/tree_sitter/parser.h — vendored (verified)
- [x] engine/core/grammars/generated/swift/parser.c — VERIFIED generated (583,715 ln; head+markers checked, standard TS output — disclosed below)
- [x] engine/core/grammars/generated/swift/scanner.c — read in full (vendored tree-sitter-swift)
- [x] engine/core/grammars/generated/swift/tree_sitter/alloc.h — vendored (verified)
- [x] engine/core/grammars/generated/swift/tree_sitter/array.h — vendored (verified)
- [x] engine/core/grammars/generated/swift/tree_sitter/parser.h — vendored (verified)
- [x] engine/core/tests/CMakeLists.txt — read in full
- [x] engine/core/tests/perfcache_env.cpp — read in full
- [x] engine/core/tests/test_astar.cpp — read in full
- [x] engine/core/tests/test_codepoint_table.cpp — read in full
- [x] engine/core/tests/test_glicko2.cpp — read in full
- [x] engine/core/tests/test_grammar_compose.cpp — read in full
- [x] engine/core/tests/test_grammar_decomposer.cpp — read in full
- [x] engine/core/tests/test_grammar_tags.cpp — read in full
- [x] engine/core/tests/test_grapheme_break.cpp — read in full
- [x] engine/core/tests/test_hash128.cpp — read in full
- [x] engine/core/tests/test_hash_composer.cpp — read in full
- [x] engine/core/tests/test_hilbert4d.cpp — read in full
- [x] engine/core/tests/test_intent_stage.cpp — read in full
- [x] engine/core/tests/test_mantissa.cpp — read in full
- [x] engine/core/tests/test_math4d.cpp — read in full
- [x] engine/core/tests/test_merkle_dedup.cpp — read in full
- [x] engine/core/tests/test_normalize_nfc.cpp — read in full
- [x] engine/core/tests/test_relation_law.cpp — read in full
- [x] engine/core/tests/test_score.cpp — read in full
- [x] engine/core/tests/test_sentence_break.cpp — read in full
- [x] engine/core/tests/test_super_fibonacci.cpp — read in full
- [x] engine/core/tests/test_text_decomposer.cpp — read in full
- [x] engine/core/tests/test_tier_tree.cpp — read in full
- [x] engine/core/tests/test_trajectory.cpp — read in full
- [x] engine/core/tests/test_version.cpp — read in full
- [x] engine/core/tests/test_word_break.cpp — read in full
- [x] engine/core/tools/ucd_tables_emit/CMakeLists.txt — read in full
- [x] engine/core/tools/ucd_tables_emit/main.cpp — read in full
- [x] engine/manifest/pos_tags.toml — read in full
- [x] engine/manifest/relation_types.toml — read in full

**Disclosed scope decision:** the two huge generated tables `sql/parser.c` (1.4M lines) and
`swift/parser.c` (584k lines) are machine-emitted LR state tables. I verified they are standard
unmodified tree-sitter output (`/* Automatically @generated */` banner, `LANGUAGE_VERSION 15`,
standard `STATE_COUNT/ts_primary_state_ids/ts_lex_modes` structure, `tree_sitter_{sql,swift}`
entrypoint) rather than reading every state row — reading 2M lines of generated jump tables yields
no audit signal. Flag if you need them fully read.

---

### OVERALL TEST VERDICT: tests are REAL, not fake
I specifically hunted for assert-nothing / tautological / mocked-away-core / populated-DB-no-op
tests. **None found.** Every test makes substantive assertions against engine behavior. Highlights
proving the suite exercises the invention's invariants:

- **test_glicko2.cpp** — pins Glickman 2013 paper intermediate values exactly (`tr.mu/phi/v/delta/
  a_value/sigma_new/phi_star/phi_new/mu_new/r_new/rd_new`, lines 104-144) and proves the closed-form
  `glicko2_fold_uniform_period` is bit-identical to the per-observation loop across 6 cases
  (`FoldUniformMatchesObservationLoop`, 269-301). `EffectiveMuDiscountsByTwoRd` asserts the
  `eff_mu = rating - 2·rd` law (227-233). Directly verifies invariant 4.
- **test_merkle_dedup.cpp** — `TrunkShortcircuitInteriorPresentSkipsItsSubtree` (170-182) and
  `...RootPresentEmitsNothing` (160-168) directly verify invariant 7's "present trunk ⟹ whole
  subtree present ⟹ skip" top-down dedup. Cross-checks shortcircuit vs filter_novel (196-216).
- **test_text_decomposer.cpp / LaplaceContentRootId** — `NormalizationFormsConverge` (78-105):
  NFC and NFD of "é" yield the SAME content id (invariants 1-2). `MultiCodepointGraphemeComposesNested`
  (198-220) proves nested-grapheme id ≠ flat-merkle id (tier = composition depth, invariant 3).
  `MatchesContentWitnessBatchRoot` (222-237) proves the lookup path and the deposit path mint the
  same id.
- **break/normalize tests** — full UAX#29/UAX#15 conformance against the official UCD `.txt` files,
  asserting `EXPECT_EQ(0u, fail)`. If the UCD files are missing, `ASSERT_FALSE(cases.empty())` fails
  loudly — so there is NO vacuous-pass path. Strong.
- **test_intent_stage.cpp** — byte-exact PG COPY-binary encoding, partition routing disjoint by
  `id.lo % N` with conservation check (541-597), 250k-row growth-from-zero no-corruption (364-393).
- **test_mantissa.cpp / test_hash128.cpp** — every-bit-probed round-trips, `MerkleTierIsNotIdentity`,
  `MerkleChildOrderMatters`, canonical-exponent invariants. Exercises identity/packing invariants.

---

### FINDINGS

**F1 — engine/core/tests/CMakeLists.txt:49 — MEDIUM — correctness (test infra brittleness)**
`set(_LAPLACE_LIBXML2_DLL "C:/Program Files/PostgreSQL/18/bin/libxml2.dll")` is a hardcoded
absolute path to a specific Postgres install/version. The POST_BUILD step copies it beside the test
exe so tests can run on Windows. Breaks for anyone without PG 18 at exactly that path (e.g. PG 17/19,
non-default install dir, the Pi target). Should derive from `find_package(LibXml2)` / the linked
import lib location, not a literal. Verified: it's a literal string with no existence guard (unlike
the UCD paths above it, which `FATAL_ERROR` when missing). Confidence: high.

**F2 — engine/core/tools/ucd_tables_emit/main.cpp:252-310 — MEDIUM — altitude + duplication**
The tool parses the UCDXML **twice**: once through `laplace_unicode_seed_compute` (native lib →
per-codepoint records, line 256) and again through its own libxml2 SAX handler `on_start`
(114-159) to build the decomposition/composition tables that go into the perfcache blob. The heavy
canonical-decomposition recursion (`full` lambda, 277-284) and the composition-exclusion logic
(ccc/comp_ex rules, 296-310) live in the **tool**, not in `laplace_core`. Two consequences:
(a) altitude violation per invariant 5 — this is substrate compose/decompose logic sitting in a
build tool; (b) the two parses can silently diverge (the records' view of a codepoint vs the blob's
decomp/compose table built here from an independent parse). It is build-time codegen so blast radius
is limited, but the decomp/compose table-build belongs behind the same native entrypoint that emits
the records. Confidence: high (traced both parse sites).
Sub-note (LOW): lines 322-323 `std::strncpy(v, version.c_str(), 8)` into `char v[8]` silently
truncates version strings >8 bytes (fine for "17.0.0", a latent trap if a longer tag is ever pinned).

**F3 — engine relation/POS entity ids use the invented `substrate/type/X/v1` namespace — MEDIUM — invention-violation (invariant 6)**
Charter invariant 6 says relation types → GWN/ConceptNet inventory name and POS → UPOS, blake3'd,
"**never an invented `substrate/type/X/v1` namespace**." The engine does exactly the forbidden thing,
and the tests pin it:
- test_relation_law.cpp:18-22 helper `type_id(name)` = `blake3("substrate/type/<name>/v1")`, then
  `UposCanonicalRoundTrip` (73-78) asserts `laplace_pos_resolve_entity("NOUN",UPOS)` ==
  `blake3("substrate/pos/NOUN/v1")`; `DeprelDynamicFamily` (80-99) asserts the DEP_NSUBJ type id ==
  `blake3("substrate/type/DEP_NSUBJ/v1")` and parent == `blake3("substrate/type/DEPENDS_ON/v1")`.
- test_grammar_compose.cpp:26 mints `type_meta = blake3("substrate/type/Meta/v1")`.
The real external name (NOUN, IS_A, DEP_NSUBJ) IS embedded, but it is wrapped in the invented
versioned namespace rather than anchored on the bare inventory name / a real registry id. This is
precisely the "corrupt convergence index — concept ids are string-walks of opaque keys" the repo's
own memory (`convergence-index-the-backbone`) flags as WS3 debt. Generating source
(`src/generated/relation_law.c`, `src/generated/pos_law.c`) is outside this bucket, but the behavior
is hard-pinned by the tests above. Note this is meta-vocabulary identity, which the memory
`vocabulary-is-content-not-anchors` records as an OPEN design question — so report as tension, not a
settled bug. Confidence: high (behavior pinned by passing-shaped assertions).

**F4 — engine/CMakeLists.txt:14-41 — LOW — build correctness (implicit toolchain pin)**
Under `if(WIN32)` the build mixes GCC/Clang-style flags (`-Wall -Wextra`) with MSVC-style flags
(`/fp:precise`, `/arch:AVX2`). Real MSVC `cl.exe` does not understand `-Wextra`; this only works with
**clang-cl** (which accepts both spellings). The Windows build is therefore implicitly pinned to
clang-cl with no documentation or `CMAKE_C_COMPILER_ID` guard saying so. Not a defect if clang-cl is
the intended toolchain, but it's undocumented coupling that will produce confusing failures under
plain MSVC. Confidence: high.

**F5 — engine/manifest/relation_types.toml:3-19 — INFO/MEDIUM — invention-tension (foundry rank debt)**
The `[ranks]` block is "Recalibrated for SEMANTIC SALIENCE": `definitional=0.97`, `taxonomic=0.90`
(so HAS_DEFINITION=0.97, IS_A=0.90). Memory `foundry-synthesis-findings` records that this exact
recalibration pushed IS_A/HAS_DEFINITION **above the 0.86 foundry band ceiling**, excluding them and
producing bigram-like GGUF exports. This is data, not code, and only manifests in the synthesis/
export path (out of this bucket), but flagging because the manifest is the source of that debt.
Confidence: medium (cross-referenced to memory + the read path; export path not in bucket).

**F6 — vendored sql/scanner.c:33-51 — INFO — third-party, out of scope**
`add_char` does `*text_size += MALLOC_STRING_SIZE; ... strncpy(tmp, text, *text_size)` — copies the
NEW (larger) size from the OLD (smaller) buffer. strncpy stops at the NUL so it's benign in practice,
but it reads with the wrong bound. This is vendored tree-sitter-sql scanner code, not Laplace's; do
not fix here. Noted for completeness.

---

### Clean (no findings)
- **engine/manifest/pos_tags.toml** — UPOS canonical set is the correct 17-tag inventory; wordnet/
  wiktionary/framenet maps all resolve to UPOS. Fully compliant with invariant 6.
- **engine/manifest/relation_types.toml** (relations themselves) — canonical names are genuine
  ConceptNet/GWN/ATOMIC/FrameNet/Unicode-UCD inventory names (IS_A, HAS_PART, CAUSES, USED_FOR,
  X_INTENT, EVOKES_FRAME, HAS_GENERAL_CATEGORY, …); aliases map surface→canonical with correct flip
  flags; de-conflation comments (IS_TYPED_AS, INHERITS_FROM, MEMBER_OF_VERBNET_CLASS kept out of IS_A)
  are architecturally sound. Compliant with invariant 6.
- **engine/core/CMakeLists.txt / grammars/CMakeLists.txt / engine/CMakeLists.txt structure** — single
  canonical trunk, no flag-gated parallel lanes/forks. The `laplace_core_static` WIN32 target is for
  SPI linkage, not a logic fork. Perfcache determinism is independently verified
  (`laplace_verify_perfcache_determinism` re-emits and `compare_files`). UCD/UCA inputs are guarded
  with `FATAL_ERROR` when missing. Good.
- **PGN grammar** (grammar.js + tree-sitter.json) — hand-authored from scratch for the chess modality,
  well-commented, SAN/castling/NAG/variation/clk-comment handling is correct; tree-sitter.json
  metadata authored as "Laplace". No invariant issue. Note for the chess-bug watch (invariant 7): the
  grammar is Stage-1 INPUT stripping only (PGN → tokens); it does NOT route board content through the
  text composer — clean at this layer.
- All 24 test files + perfcache_env.cpp — real, substantive, no fakes (see OVERALL TEST VERDICT). The
  tests/CMakeLists source list matches the test files exactly; no test silently dropped from the build.
- No Claude-authored disparagement tags (DEAD/broken/noisy/etc.) found in any file in this bucket.
- No dead/unused code found.

---

### Bucket summary
- Findings: CRITICAL 0 · HIGH 0 · MEDIUM 3 (F1, F2, F3) · LOW 1 (F4) · INFO 2 (F5, F6)
- Tests: REAL across the board — strong, on-mandate coverage of identity/dedup/tier/Glicko-2/
  geometry/conformance invariants. No fake or vacuous tests.
- **Worst issue: F3** — the engine mints relation-type and POS entity ids in the invented
  `substrate/type/X/v1` / `substrate/pos/X/v1` namespace that invariant 6 explicitly forbids, pinned
  by the tests. It corrupts the convergence index (matches the repo's own WS3 debt note) even though
  the real inventory name is embedded inside the string. (Treat as the documented-open meta-vocab
  identity question, not a settled bug.) Honorable mention F2 — UCDXML parsed twice with compose/
  decompose logic stranded in a build tool instead of laplace_core.
