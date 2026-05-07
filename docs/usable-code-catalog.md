# Catalog of Actually-Usable Code (Evidence-Based)

This document is built only from files I read directly. Every claim cites the file path and the line numbers I read from. Documentation files (CLAUDE.md, AGENTS.md, .claude/rules/, READMEs, status docs, evidence/ summaries) are not used as evidence here.

## Scope

**Files I verified by direct read** (used as evidence in this catalog):
- `Hartonomous-000/extension/src/elo.c` (lines 1-29)
- `Hartonomous-000/extension/src/hnsw.c` (lines 1-80)
- `Hartonomous-000/extension/src/firefly.c` (lines 1-39)
- `Hartonomous-002/ext/hartonomous_pg/include/hartonomous_pg/stubs.h` (full, 25 lines)
- `Hartonomous-002/ext/hartonomous_pg/src/firefly/firefly.c` (full, 14 lines)
- `Hartonomous-002/ext/hartonomous_pg/src/glicko2/glicko2_core.c` (full, 144 lines)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Ucd/SuperFibonacci.cs` (full, 43 lines)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Ucd/UcdUcaDecomposer.cs` (full, 213 lines)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/WordNet/WordNetDecomposer.cs` (full, 256 lines)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Tatoeba/TatoebaDecomposer.cs` (lines 1-100)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Wiktionary/WiktionaryDecomposer.cs` (lines 1-100)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Model/ModelDecomposer.cs` (lines 1-77)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Model/DecoderOnlyDecomposer.cs` (full, 54 lines)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Model/TransformerWeightPipeline.cs` (lines 1-120)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Model/TokenizerAssetDecomposer.cs` (lines 1-100)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Model/TensorReader.cs` (lines 1-80)
- `Hartonomous-002/src/managed/Hartonomous.Decomposers/Modality/PngStructuralDecomposer.cs` (lines 1-80)
- `Hartonomous-001/src/Hartonomous.Decomposers/Safetensors/LaplacianEigenmap.cs` (lines 1-100)
- `Hartonomous-001/src/Hartonomous.Decomposers/Safetensors/Passes/EmbeddingFireflyPass.cs` (lines 1-120)

**Files I did NOT read** (so cannot make verified claims about them — stated as a scope honesty marker, not an absence-of-evidence claim):
- All other Hartonomous-001 decomposer files (~70+ files including all WordNet/OMW/UD/Wiktionary/Tatoeba/Iso639 parsers, ~30 Safetensors analysis passes)
- All Hartonomous-000 cli/ and common/ C++ implementation files
- Hartonomous-002 MoeFamilyDecomposer, MoeMlaFamilyDecomposer, HuggingFacePackageDecomposer, OmwDecomposer, UdDecomposer, AtomicDecomposer, WavStructuralDecomposer, AudioArtifactDecomposer, ImageArtifactDecomposer, TextArtifactDecomposer, modality WavParser/PngParser/RunDetector
- Hartonomous-002 inference/recomposers/recipe code
- Native C extensions in -001 and -002 beyond what's listed above

This is a sample audit of strategically-chosen files, not a complete inventory. Findings extrapolate carefully.

## Synthesis criteria (for grading)

From `substrate-synthesis.md`:

1. Universal Unicode codepoint atom pool — tier 0 across every modality is codepoints
2. Edges reference entities — not hardcoded English type labels
3. GEOMETRY4D parallel type family — not GeometryZM with M repurposed
4. Three-layer Glicko-2 — rated-source attestation, not negative sampling
5. Semantic decomposition all the way down — no binary blobs at any tier
6. AI model ingestion = semantic edge extraction — not weight storage
7. Cross-modal dedup via shared atoms — automatic from substrate structure
8. Real algorithms, not stubs

---

## Per-file findings

### USABLE (real code, broadly aligned with synthesis)

#### `Hartonomous-002/ext/hartonomous_pg/src/glicko2/glicko2_core.c`

**What it does** (verified): Pure C implementation of Glicko-2 update following Glickman (2013). Functions `g_func` (line 17), `E_func` (line 23), `illinois_solve_volatility` (line 29-78), and `hartonomous_glicko2_apply` (line 80-144). Variable names match the paper. Step 5 Illinois method bracketed solver for new volatility. Step 6 RD update. Steps 1-2 internal-scale conversion at line 88-91. Output back to display scale at line 137-141. Handles zero-opponents case at line 93-103 (pre-rating-period RD update only).

**Verdict: USABLE — real Glicko-2.** This is the one piece of -002 native code that's a real algorithm, not a stub. Paper-faithful. Reusable as the substrate's canonical Glicko-2 kernel.

**Issues against synthesis**: None I can see. This is a single-pair Glicko-2 update; the three-layer rated-source-attestation logic (sources/entities/edges) would compose around it, not replace it.

---

#### `Hartonomous-002/src/managed/Hartonomous.Decomposers/Ucd/SuperFibonacci.cs`

**What it does** (verified): Computes deterministic 4D point on S³ for codepoint `i` of `total` codepoints. Constants `Phi = 1.5343237490380328129` and `Psi = 1.6180339887498948482` (lines 15-16). Math at lines 25-34 produces `(x, y, z, m)` in 4D. Normalization to unit sphere at lines 36-40. Returns `Point4d(X, Y, Z, M)` record.

**Verdict: USABLE — real super-Fibonacci on S³.** Matches the synthesis claim of UCD-driven super-Fibonacci codepoint placement. Lightweight, deterministic, reusable.

**Issues against synthesis**:
- The class doc (line 8-12) claims this is a "managed mirror of the native Super-Fibonacci spiral" with the C side being source of truth (`hartonomous.super_fibonacci_4d`). Cross-language alignment is a maintenance hazard but not an architectural error.
- The placement is purely positional — does NOT incorporate UCD properties (script, category, decomposition, casing, Unihan radicals) into the *ordering* the way the synthesis describes. It places codepoints at sequential positions; semantic clustering would require sequencing codepoints by UCD properties first, then super-Fibonacci. Caller would have to do that sequencing.

---

#### `Hartonomous-002/src/managed/Hartonomous.Decomposers/Ucd/UcdUcaDecomposer.cs`

**What it does** (verified):
- Parses UCD `UnicodeData.txt` into row records (line 79)
- Hashes each codepoint to an AtomId via `_hasher.HashCodepointBatch` (line 83-88)
- Looks up entity_type_id, edge_type_id, role_id, provenance_id from sink (lines 90-96)
- For each codepoint row: emits an entity (line 109), computes super-Fibonacci position and emits physicality (lines 111-114), upserts codepoint facts (line 116)
- Emits canonical decomposition edges if present (lines 119-145), with role-typed members
- Emits case-fold edges (lines 147-159)
- Returns counts of emitted entities, decomposition edges, case-fold edges

**Verdict: USABLE for UCD ingestion mechanics; PARTIAL alignment with synthesis.**

**Issues against synthesis**:
- Hardcoded English entity type code `"codepoint"` and edge type codes `"canonical_decomposition_of"`, `"case_folds_to"` looked up against schema reference tables (lines 90, 93-94)
- Hardcoded role names `"source"`, `"target"` (lines 95-96)
- Schema model assumed: there's an `entity_type` reference table with `int` IDs, an `edge_type` reference table with `int` IDs, and an `edge_role` reference table with `int` IDs. The synthesis says edge types should be substrate entities themselves, not int-keyed reference rows.
- Codepoint placement uses `SuperFibonacci.At(row.Codepoint, totalCodepoints)` (line 111) — the codepoint's *integer* is the index. This means the codepoint's UCD properties (script, category, etc.) DON'T influence its S³ position; placement is positional only. Synthesis describes UCD-property-driven sequencing. The decomposer doesn't do that.
- Identity is via native hasher, but the decomposer doesn't decompose codepoints into anything below them (correctly — codepoints ARE atoms).

**Reusable parts**: parser invocation, super-Fibonacci position computation, decomposition-edge and case-fold-edge emission logic. The hardcoded type codes and the schema-coupling would need to change to match the synthesis.

---

#### `Hartonomous-001/src/Hartonomous.Decomposers/Safetensors/LaplacianEigenmap.cs` (lines 1-100 read)

**What it does** (verified):
- Projects N×D embedding matrix to 3D via Laplacian eigenmap
- L2-normalizes input rows in place (lines 39-54) for cosine similarity
- Builds symmetric k-NN cosine graph via `KnnCosineGraph.BuildF64(n, d, flat, options.K)` (line 59) — comment at line 57: "Exact symmetric k-NN cosine graph via the facade"
- Computes degree vector (lines 64-73) and degree-inverse-square-root (lines 74-78)
- Computes normalized affinity matrix `M = D^(-1/2) · W · D^(-1/2)` (lines 80-89)
- Drops lower triangle for upper-triangle-only Lanczos input (lines 91-100, comment cites `ext/libhartonomous/tests/test_sparse_eigs.cc::FullMiniLmChainCrashRepro`)
- Returns `(double[] X, double[] Y, double[] Z)` — three eigenvector projections

**Verdict: USABLE — real Laplacian eigenmap.** Uses exact k-NN (matches synthesis requirement, contradicts -000's HNSW). Real normalized-Laplacian math. Comments reference a real crash-repro test that was fixed. The projection is to 3D (X, Y, Z); the EmbeddingFireflyPass adds the M coordinate as L2 magnitude per row (see EmbeddingFireflyPass below) — so the combined output is 4D, matching the synthesis.

**Issues against synthesis**:
- `KnnCosineGraph.BuildF64` is called as a "facade" — I haven't verified whether the underlying implementation is exact or approximate. The comment claims exact; the user wanted exact. Would need to read the native code to confirm.
- Returns 3D arrays plus magnitude as M = effectively 4D. The synthesis says positions live in the 4-ball with radial coordinate as coherence. Magnitude as M sort of matches that intuition but isn't the same construction.
- Doesn't perform Gram-Schmidt orthonormalization in this file — synthesis described that as a step. Either it's done elsewhere or it's missing.

**Reusable parts**: the row-normalization, normalized-Laplacian construction, upper-triangle conversion. The KNN call delegates to a facade — need to verify that facade is exact before relying on the whole pipeline.

---

#### `Hartonomous-002/src/managed/Hartonomous.Decomposers/Model/TokenizerAssetDecomposer.cs` (lines 1-100 read)

**What it does** (verified):
- Reads tokenizer.json bytes from path (line 66)
- Parses HuggingFace tokenizer.json into vocab + added_tokens (line 68)
- For each (token_surface, vocab_index): calls `TextDecomposer.DecomposeReturningDocumentAtomAsync(...)` to get a document atom hashed by the surface bytes (referenced at lines 30-37 in docstring)
- Emits `tokenizer_vocab_entry` entity per vocab index, `vocab_position_of` edge from each entry to the document atom (lines 32-34 in docstring)
- Builds `IVocabAtomResolver` mapping vocab index → substrate atom

**Verdict: USABLE PATTERN — correctly content-addresses tokens through TextDecomposer.** The class-doc claim at lines 16-23 is the architectural punchline: "Two tokenizers with the same token surface ('dog' in vocab[1234] of Llama and in vocab[5678] of Qwen) converge on the SAME document atom because TextDecomposer's CompositionId is content-addressed." This is the correct cross-model dedup pattern from the synthesis.

**Issues against synthesis**:
- I haven't read TextDecomposer.cs itself, so I can't verify it actually decomposes through codepoints (vs. doing an opaque hash of UTF-8 bytes). The dedup property holds either way (content-addressed), but the codepoint-decomposition property requires verification.
- Hardcoded English entity type codes `"tokenizer_vocab_entry"`, `"document"` and edge type code `"vocab_position_of"` looked up at lines 71-75
- The "tokenizer_vocab_entry" wrapper entity is questionable — the synthesis would say the document atom directly carries vocabulary-position edges via provenance, not that there's a separate wrapper entity per index

**Reusable parts**: the orchestration pattern (parse JSON → loop vocab → call text decomposer → emit binding edges) and the cross-model dedup property are correct.

---

#### `Hartonomous-002/src/managed/Hartonomous.Decomposers/Modality/PngStructuralDecomposer.cs` (lines 1-80 read)

**What it does** (verified, based on docstring lines 9-30 plus reading lines 30-80):
- Layer 2: emits `channel_value_uint8` atoms — exactly 256 distinct rows total (one per byte value 0-255). Universal vocabulary.
- Layer 2.5: emits `pixel_value_rgba8` = `CompositionId([R_atom, G_atom, B_atom, A_atom])` over channel atoms. Identical pixels across all images converge to one row.
- Layer 3: tiles pixels into 16×16 regions; identical tiles share one row across all images.
- Layer 4: image_grid as composition of tile atoms.
- Implementation orchestration at lines 42-62 calls EmitLayer2AndLayer2_5Async, EmitLayer3RegionsAsync, EmitLayer4GridAsync in sequence.

**Verdict: USABLE — semantic decomposition with the right structure for images.** Channel-value atoms are deduplicated to 256. Pixels are compositions, not blobs. Tiles dedupe across images. This matches the synthesis pattern of pixels-as-compositions and cross-image dedup at every tier.

**Issues against synthesis**:
- The `channel_value_uint8` atoms are NOT decomposed through codepoints — they're a separate atom type (256 rows). The synthesis says even pixel values like 255 should decompose through digit codepoints `[2,5,5]`. PngStructuralDecomposer treats byte values as their own atom pool. This is a slight architectural deviation but the cross-image dedup property is achieved.
- Tile size hardcoded to 16×16 (line 33) — fixed sizing rather than content-driven.
- Hardcoded English entity type codes for the layer atom types.

**Reusable parts**: the hierarchical composition pattern (channel → pixel → tile → image_grid), the cross-image dedup at every layer, the streaming decomposition.

---

#### `Hartonomous-002/src/managed/Hartonomous.Decomposers/Tatoeba/TatoebaDecomposer.cs` (lines 1-100 read)

**What it does** (verified):
- Streams `sentences.csv` as TSV (id, lang, text) at lines 70-89
- For each row, calls `_textDecomposer.DecomposeReturningDocumentAtomAsync(new TextInput(text, ProvenanceCode), _sink, cancel)` (lines 82-84) — text content goes through codepoint decomposition
- Returned document atom is used as the sentence atom (line 86): `await _sink.WriteEntityAsync(new EntityRecord(entityTypeIdSentence, documentAtom), cancel)`
- Streams `links.csv` as TSV (source_id, target_id) at lines 91-100, emitting `translation_of` edges

**Verdict: USABLE PATTERN — uses TextDecomposer for content-addressed cross-source dedup.** Class doc line 24-25: "Per Substrate Law 1, identical sentence bytes converge across languages and across corpora -- a Tatoeba 'yes' surface and a Wiktionary example 'yes' share a substrate row." This is the synthesis's cross-source dedup principle.

**Issues against synthesis**:
- Same TextDecomposer-verification gap as TokenizerAssetDecomposer
- Hardcoded entity type code `"tatoeba_sentence"` and edge type `"translation_of"` (lines 61-62)
- Wraps document atom as a separate `tatoeba_sentence` entity — same wrapper pattern; synthesis would say the document atom is the sentence, with provenance edges marking it as Tatoeba-sourced

**Reusable parts**: the TSV streaming, TextDecomposer-routing pattern, translation edge emission.

---

#### `Hartonomous-002/src/managed/Hartonomous.Decomposers/Model/TensorReader.cs` (lines 1-80 read)

**What it does** (verified):
- `ElementSizeBytes` for safetensors dtypes: F64=8, F32=4, BF16/F16=2, F8_E4M3/F8_E5M2=1, I64=8, I32=4, I16=2, I8/U8=1 (lines 13-24)
- `StreamFloat32Rows` reads tensor body as float32 in row-major chunks (lines 37-63)
- `DecodeBlock` handles dtype-specific decode: F32 direct cast (line 70), F16 via BitConverter.UInt16BitsToHalf (lines 73-77), BF16 partial (line 78+)
- Class doc line 8: "Lossless tensor decode to float32 (Substrate Law 11)"

**Verdict: USABLE — real, working tensor reader utility.** Streams in chunks (avoids loading whole tensor). Dtype handling correct. Reusable directly.

**Issues against synthesis**: This is a utility, not a decomposer. It's an honest tool. No architectural conflicts.

**Reusable parts**: all of it — keep this file as-is.

---

### PARTIAL (real code with significant deviations from synthesis)

#### `Hartonomous-001/src/Hartonomous.Decomposers/Safetensors/Passes/EmbeddingFireflyPass.cs` (lines 1-120 read)

**What it does** (verified from what I read):
- Class doc lines 10-37 describes the operation: for each Track-1 token-embedding tensor, decode bytes to f64, compute per-row L2 magnitude (M coordinate), project rows to first 3 non-trivial Laplacian eigenvectors (X, Y, Z), look up the substrate `bpe_token` entity hashed by token bytes, attach `embedding_firefly` physicality (POINTZM WKB) tagged by model provenance
- Per-tensor seed derivation (lines 110-114): `tensorSeed = baseSeed XOR low 64 bits of tensor content hash`
- Calls `LaplacianEigenmap.Project(flat, rows, cols, opts, ...)` (lines 116-118) and gets back X, Y, Z arrays
- Loads tokenizer to map vocab_index → token_bytes (lines 64-79); skips with warning if no tokenizer (lines 73-77, lines 70-71 in docstring) rather than fabricating ghost entities

**Verdict: USABLE-ARCHITECTURE, PARTIAL CODE** — the architectural pattern is correct: tokens become substrate atoms hashed by their bytes, fireflies are physicality on those atoms tagged by source model. Cross-model agreement materializes via shared bpe_token entities.

**Issues against synthesis**:
- POINTZM (the PostGIS GeometryZM type) is what physicality stores. The user's synthesis is explicit that GEOMETRY4D should be a parallel type family, not GeometryZM with M repurposed. -001 uses GeometryZM throughout.
- Hardcoded `bpe_token` entity type, `embedding_firefly` physicality type
- "Track 1" / "Track 2" terminology comes from -001's documentation; whether it appears in the synthesis is an open question (haven't verified). It's at minimum framework-y.
- `TokenizerModel? tokenizer = TryLoadTokenizer(...)` (line 72) and skip-if-no-tokenizer behavior: correct (don't fabricate) but means models without tokenizer.json get no fireflies emitted. Probe-driven extraction independent of the tokenizer artifact would be more robust.
- The `ModelDerivedTrustMu = 60_000.0` constant (line 47) is a hardcoded source rating mu value — synthesis says model source ratings should be entity-stored and updateable, not hardcoded constants in pass code.

**Reusable parts**: the orchestration pattern (per-tensor seed, deterministic project, anchor to vocab atoms via tokenizer), the LaplacianEigenmap.Project call, the dedup-by-token-bytes anchoring.

---

#### `Hartonomous-002/src/managed/Hartonomous.Decomposers/WordNet/WordNetDecomposer.cs` (full read)

**What it does** (verified):
- Regex parses Princeton WordNet `data.{noun,verb,adj,adv}` files (line 32-34)
- Pass 1 (lines 78-124): for each synset record, hashes lemma names via `HashLemma(word)` returning `_hasher.Hash(UTF8.GetBytes("omw_lemma|eng|{word}"))` (lines 181-187), hashes synset via `HashPrincetonSynset(offset, pos)` returning `_hasher.Hash(UTF8.GetBytes("princeton_wordnet_3_0|synset|{pos}|{offset:D8}"))` (lines 193-197), emits `lemma` entities, `synset` entities, `synset_of` edges
- Pass 2 (lines 130-171): for pointer relations, dispatches Princeton symbols `@` → hypernym_of, `~` → hyponym_of, `%m/%s/%p` → meronym_of, `#m/#s/#p` → holonym_of, `!` → antonym_of (lines 144-156); emits typed edges between synset entities

**Verdict: WORKS BUT WRONG ARCHITECTURE.** The decomposer functions — it parses WordNet files, emits entities and edges, will load data into a substrate. But the identity model conflicts with the synthesis on multiple points.

**Issues against synthesis**:
- **Lemma identity is a flat byte hash of `"omw_lemma|eng|{word}"`** (lines 184-186). NOT decomposed through codepoints. The synthesis says lemma `dog` should be a tier-2 entity composed of codepoint atoms `[d, o, g]`. Two decomposers using different prefixes would NOT dedupe even on the same word.
- **Hardcoded English language prefix `eng`** baked into lemma identity (line 185). Cross-language dedup is broken by construction.
- **Synset identity is opaque metadata hash** of `"princeton_wordnet_3_0|synset|{pos}|{offset:D8}"` (line 196). Synset content (member words, gloss, hypernyms) does NOT enter the hash. Two synsets with identical content from different sources would NOT dedupe.
- **Hardcoded English edge type codes** `"synset_of"`, `"hypernym_of"`, `"hyponym_of"`, `"meronym_of"`, `"holonym_of"`, `"antonym_of"` looked up against schema reference table at lines 57-62.
- **Meronym/holonym flavors collapsed**: `%m` (member), `%s` (substance), `%p` (part) all map to one `meronym_of` edge type (lines 149-151). Information loss; per-flavor edges would preserve the distinction.
- Comment at line 25 acknowledges: "Other symbols (+, =, ;c, etc.) are skipped until they are seeded in ref.edge_type." — known incomplete.

**Reusable parts**: the regex parse, the file-iteration scaffolding, the pointer-to-edge dispatch logic. The identity recipes need to change entirely to match the synthesis.

---

#### `Hartonomous-002/src/managed/Hartonomous.Decomposers/Wiktionary/WiktionaryDecomposer.cs` (lines 1-100 read)

**What it does** (verified):
- Streams kaikki.org `raw-wiktextract-data.jsonl` line by line (line 59-61)
- Parses each JSON object, extracts `word`, `lang_code`, `pos` (lines 70-74)
- Hashes lemma via `HashLemma(lang, word)` — uses same `"omw_lemma|{lang}|{form}"` recipe as WordNetDecomposer (per docstring line 17, "to share identity with WordNet / OMW")
- Caches lemma to avoid duplicate writes (line 77)
- Hashes POS atom via `_hasher.Hash(UTF8.GetBytes($"part_of_speech|{pos}"))` (line 85)
- Emits `lemma` entity (line 79), `part_of_speech` entity (line 87), `has_pos` edge (lines 89-94)
- Iterates `translations` array if present (lines 97-100, partial read)

**Verdict: WORKS BUT WRONG ARCHITECTURE — same pattern as WordNetDecomposer.**

**Issues against synthesis**:
- Same lemma identity recipe `"omw_lemma|{lang}|{form}"` — dedupes WITH WordNet via shared canonical string but NOT through codepoint decomposition. Hardcoded language prefix.
- POS atom hashed as `"part_of_speech|{pos}"` opaque flat hash. Synthesis would have part-of-speech be a concept entity referenced by edges, not a stringly-hashed atom.
- Hardcoded `"lemma"`, `"part_of_speech"` entity types and `"translation_of"`, `"has_pos"` edge types looked up against schema (lines 46-49).

**Reusable parts**: JSONL streaming, kaikki entry parsing, POS extraction. Identity recipes need to change.

---

#### `Hartonomous-002/src/managed/Hartonomous.Decomposers/Model/TransformerWeightPipeline.cs` (lines 1-120 read)

**What it does** (verified):
- Shared transformer weight-walker pipeline used by DecoderOnlyDecomposer / MoeFamilyDecomposer / MoeMlaFamilyDecomposer
- Computes opaque model entity hash from `"{arch}|{dirName}|{hiddenSize}|{numHiddenLayers}|{vocabSize}"` (lines 110-117)
- Iterates all tensors via `ctx.Manifest.EnumerateTensors()` (line 60)
- Resolves each tensor name to a role via `TensorRoleMap.Resolve(tensorName, family)` (line 63); skips if `RoleCode == "unknown_role"` (lines 64-68)
- For `token_embedding` role: calls `EmitTrack1FirefliesAsync` (line 70-72) — but the docstring at lines 16-19 admits this is a placeholder: "M8-pre uses the deterministic Super-Fibonacci spiral so the gate D-firefly-count contract holds without spectral compute. M8-close swaps in the Laplacian eigenmap projection."
- Emits Track 2 typed edges via `EmitTrack2EdgesAsync` (line 79-81), filtered by `Track2SignificanceThreshold` (line 58)
- Edge type for role dispatched in `EdgeTypeForRole` (lines 92-106) with hardcoded English role-to-edge mapping: attention_q/k/v/o → `beaten_path`, ffn_*/moe_* → `transformation`, lm_head → `hidden_to_token`, rms_norm/layer_norm → `layer_norm`

**Verdict: PARTIAL — Track 2 (weight tensors → edges) is real; Track 1 (embedding fireflies) is a deliberate placeholder.**

**Issues against synthesis**:
- **Track 1 firefly emission is acknowledged in code docstring as a Super-Fibonacci placeholder (line 16-19) with the real Laplacian eigenmap projection deferred to "M8-close" (uncompleted milestone).** This is the failure pattern of using placeholder code to satisfy validation gates. The real eigenmap exists in -001's LaplacianEigenmap.cs but is not invoked here.
- Model identity is opaque metadata hash (line 110-117), not content-addressed
- Hardcoded English edge types: `"beaten_path"`, `"transformation"`, `"hidden_to_token"`, `"layer_norm"` (lines 46-49)
- TensorRoleMap dispatches by hardcoded English role strings (lines 95-104)
- Track 2 emits edges only for tensor entries above threshold — no per-edge Glicko-2 rating attached to my reading of these 120 lines
- "Track 1" / "Track 2" framework is -001's invention, carried forward; synthesis doesn't specify these tracks

**Reusable parts**: the iteration scaffolding, the role-based dispatch pattern. The actual emission logic depends on the schema model and contains the firefly-placeholder issue.

---

### STUB / NOT-USABLE

#### `Hartonomous-002/ext/hartonomous_pg/include/hartonomous_pg/stubs.h` (full read)

**What it does** (verified): Defines `HARTONOMOUS_NOT_IMPLEMENTED(fn_name, milestone_id)` macro (line 15-23) that calls `ereport(ERROR, ...)` with "feature not supported" + "Owned by milestone X" message and `PG_RETURN_NULL()`.

**Verdict: STUB INFRASTRUCTURE.** Not a useful thing in itself; it's the macro that other "implementations" call. Its existence is the documented design — every PG_FUNCTION_INFO_V1 that needs implementation has a stub entry that calls this. Functions error at runtime instead of returning garbage, but they don't function.

**Issues against synthesis**: The whole pattern is wrong. Either the function exists and works, or it doesn't exist. Stubs that error don't satisfy "real algorithms not stubs."

---

#### `Hartonomous-002/ext/hartonomous_pg/src/firefly/firefly.c` (full read)

**What it does** (verified): Single PG function `hartonomous_firefly_project` whose body is one line: `HARTONOMOUS_NOT_IMPLEMENTED("firefly_project", "M8");` (line 14).

**Verdict: STUB.** The Laplacian eigenmap projection — the heart of model embedding ingestion in the substrate — is unimplemented at the native PG layer in -002. -001 has a managed-side LaplacianEigenmap.cs that is real; -002's native firefly is a stub.

---

#### `Hartonomous-000/extension/src/elo.c` (full read)

**What it does** (verified): Three PG functions wrapping `h_elo_expected`, `h_elo_update`, `h_elo_k` calls (lines 6-29). ELO rating math in core C library.

**Verdict: WRONG ALGORITHM.** ELO does not have rating deviation, volatility, or the three-layer rated-source structure the synthesis requires. Not usable as the substrate's rating layer. The h_elo_* functions in core could be repurposed but the data model would be wrong.

---

#### `Hartonomous-000/extension/src/hnsw.c` (lines 1-80 read)

**What it does** (verified): PG functions wrapping HNSW (Hierarchical Navigable Small World) approximate-nearest-neighbor index. `pg_hnsw_create` (lines 28-51), `pg_hnsw_add` (lines 53-76+). Session-level HNSW handle (line 8).

**Verdict: WRONG ALGORITHM.** The synthesis explicitly requires exact KNN for Laplacian eigenmap construction. HNSW is approximate. The user explicitly stated this in our session. Not usable.

---

#### `Hartonomous-000/extension/src/firefly.c` (lines 1-39 read)

**What it does** (verified): PG function `pg_project_to_4d` (lines 6-39). Takes flat float array + src_dim + k_neighbors + heat_t. Calls `h_project_to_4d(...)` (line 22). Wraps result as POINTZM array via `pointzm_from_4d(...)` (line 29). Constructs PostGIS geometry array as result.

**Verdict: REAL WRAPPER, INHERITS WRONG TYPES.** This file is a real PG wrapper around a native projection function. The native `h_project_to_4d` may or may not be a real Laplacian eigenmap (would need to verify in core/). The wrapper outputs PostGIS POINTZM (not GEOMETRY4D parallel type). Architecture-wrong on the type system but functionally connected.

**Reusable parts**: the wrapper pattern. The output type would need to change.

---

### NOT VERIFIED (acknowledged, not assessed)

I did not read these files. I cannot make claims about them. They might be real, partial, or stubs — unknown until read:

- All Hartonomous-001 decomposer files except LaplacianEigenmap.cs and EmbeddingFireflyPass.cs (~70+ files)
- All Hartonomous-001 Safetensors analysis passes except EmbeddingFireflyPass.cs (~30 files)
- All Hartonomous-000 cli/ and common/ implementation files (~30+ files)
- Hartonomous-002 MoeFamilyDecomposer.cs, MoeMlaFamilyDecomposer.cs, HuggingFacePackageDecomposer.cs
- Hartonomous-002 OmwDecomposer.cs, UdDecomposer.cs, AtomicDecomposer.cs, TextArtifactDecomposer.cs
- Hartonomous-002 Modality WAV/audio/image/text artifact decomposers
- Hartonomous-002 inference, recomposers, recipes, generation
- Hartonomous-002 native extension files beyond firefly.c, glicko2_core.c, stubs.h

Some of these may turn out to be additionally usable; some may turn out to be additionally broken. Without reading them, I won't claim either.

---

## Summary table

| Component | File | Verdict | Reusable? |
|---|---|---|---|
| Glicko-2 kernel | `H-002/ext/.../glicko2_core.c` | USABLE | Yes, directly |
| Super-Fibonacci managed | `H-002/.../Ucd/SuperFibonacci.cs` | USABLE | Yes, with caveat |
| UCD decomposer | `H-002/.../Ucd/UcdUcaDecomposer.cs` | PARTIAL | Logic yes, schema model no |
| Laplacian eigenmap | `H-001/.../LaplacianEigenmap.cs` | USABLE | Yes (with KNN-exactness re-verification) |
| Tokenizer asset decomposer | `H-002/.../TokenizerAssetDecomposer.cs` | USABLE PATTERN | Pattern yes, schema model no |
| PNG structural decomposer | `H-002/.../PngStructuralDecomposer.cs` | USABLE | Pattern yes |
| Tatoeba decomposer | `H-002/.../TatoebaDecomposer.cs` | USABLE PATTERN | Pattern yes, identity recipe TBD |
| Tensor reader | `H-002/.../TensorReader.cs` | USABLE | Yes, directly |
| Embedding firefly pass | `H-001/.../EmbeddingFireflyPass.cs` | PARTIAL | Pattern yes, GeometryZM no |
| WordNet decomposer | `H-002/.../WordNetDecomposer.cs` | WORKS, WRONG ARCH | Parsing yes, identity recipes no |
| Wiktionary decomposer | `H-002/.../WiktionaryDecomposer.cs` | WORKS, WRONG ARCH | Parsing yes, identity recipes no |
| Transformer weight pipeline | `H-002/.../TransformerWeightPipeline.cs` | PARTIAL | Track 2 logic yes, Track 1 is placeholder |
| Stubs macro | `H-002/ext/.../stubs.h` | STUB | No |
| Native firefly | `H-002/ext/.../firefly/firefly.c` | STUB | No |
| ELO | `H-000/.../elo.c` | WRONG ALGORITHM | No |
| HNSW | `H-000/.../hnsw.c` | WRONG ALGORITHM | No |
| Native firefly (000) | `H-000/.../firefly.c` | REAL WRAPPER, WRONG TYPES | Pattern yes |

---

## Direct answer to the question: do you have any actual real decomposers that respect your invention?

**No, not fully.** Of the decomposers I read directly:

- The decomposers that USE the right pattern (TextDecomposer-routed for content-addressed cross-source dedup) — TokenizerAssetDecomposer, TatoebaDecomposer — DO route content through a text decomposer for codepoint-based identity. But I haven't read TextDecomposer itself to confirm it actually decomposes through codepoints rather than doing an opaque UTF-8 hash. Cross-source dedup will work either way; codepoint atom convergence requires verification.

- The seed decomposers I read (WordNetDecomposer, WiktionaryDecomposer) DO NOT route lemmas through a text decomposer. They hash via `"omw_lemma|eng|{word}"` canonical strings. They share identity with each other via the same recipe but they break the synthesis's universal-codepoint-atom property.

- The UCD decomposer I read (UcdUcaDecomposer) DOES place codepoints on S³ via super-Fibonacci, but the placement uses the codepoint integer as the index — UCD properties don't influence ordering. Synthesis describes UCD-property-driven sequencing.

- The model decomposer (TransformerWeightPipeline) emits Track 1 fireflies via Super-Fibonacci PLACEHOLDER instead of the real Laplacian eigenmap — explicit code comment confirms this. Track 2 weight-as-edge emission is real but uses hardcoded English role-to-edge-type mapping.

- The native firefly (the C-side eigenmap) is a stub in -002 (`HARTONOMOUS_NOT_IMPLEMENTED`). The managed-side LaplacianEigenmap in -001 is real and correct.

**The shape of "real decomposers that respect the invention" exists in fragments**: the LaplacianEigenmap math (-001), the SuperFibonacci placement (-002), the Glicko-2 kernel (-002), the cross-source-dedup pattern via text decomposer (-002), the PNG layered decomposition (-002), the lossless tensor reader (-002). None of these are integrated into a working pipeline that respects all of the synthesis principles. The integration that exists is built on a schema model (hardcoded entity_type / edge_type / role / provenance reference tables) that conflicts with the synthesis's "edges reference entities" principle.

**Practically reusable, with rework**:
- Glicko-2 kernel — drop-in
- Tensor reader — drop-in
- Super-Fibonacci managed implementation — drop-in (with potential re-verification against native)
- Laplacian eigenmap math (the Project method) — drop-in if KnnCosineGraph.BuildF64 is verified exact
- The patterns from TokenizerAssetDecomposer and TatoebaDecomposer (route content through text decomposer for cross-source dedup)
- PNG structural decomposition pattern (channel → pixel → tile → grid composition with cross-image dedup)
- Parser scaffolding from each seed decomposer (separate from the identity hashing)

**Not reusable as-is**:
- Anything depending on the schema model with hardcoded entity_type / edge_type / role IDs as int FKs
- The lemma/synset/POS identity recipes that hardcode language prefixes and bypass codepoint decomposition
- The TransformerWeightPipeline as currently wired (firefly placeholder)
- Anything ELO-based (whole rating system needs to be Glicko-2)
- Anything HNSW-based (whole approximate-nearest-neighbor needs to be exact)

**Unknown without reading more**: the bulk of -001 (~70+ unread files including 30+ Safetensors analysis passes), most of -000 (C++ implementation), several -002 modality and model decomposers.
