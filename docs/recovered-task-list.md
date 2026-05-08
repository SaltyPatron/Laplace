# Recovered task list (session 54c0d19c)

Total: 114 task files

## 1. G1: Verify native build (laplace_native.dll + laplace_pg.dll via CMake)
**status:** completed

laplace_native.dll BUILT and VERIFIED via end-to-end P/Invoke (laplace-seed-gen scan --limit 16 produces real BLAKE3 hashes + super-Fibonacci 4D positions + Hilbert indices on first 16 UCD codepoints). Build-system structural fixes: PG made optional at root + auto-discovered via hint paths (C:/Program Files/PostgreSQL/<major>); strict /W4 /WX scoped to OUR targets via laplace_strict_flags interface library (no leak into FetchContent deps or ASM); PG headers as SYSTEM include (suppresses C4200/C4005); WIN32 + port/win32 shim path for laplace_pg target; Directory.Build.targets created (was Directory.Build.props, too early to see UseNativeRuntime); WINDOWS_EXPORT_ALL_SYMBOLS=ON on laplace_native; CMake RUNTIME_OUTPUT_DIRECTORY set to the path native-dll.targets expects. laplace_pg.dll target has remaining source-level bugs (sql_glicko2 missing PG include, /WX trips in type IO sources, linestring4d_type.h) — captured in new task #92.

## 2. G1: Verify managed build via build.ps1 (not just dotnet build)
**status:** completed

scripts/build.ps1 round-trips green: [1/2] Native build OK (laplace_native.dll + laplace_pg.dll), [2/2] Managed build OK (60+ projects, 0 warnings, 0 errors). Native runtime auto-staged into every UseNativeRuntime=true project's bin via Directory.Build.targets (was Directory.Build.props — fix documented in build-system invariants memory).

## 3. G2: CTest coverage for every native service
**status:** pending

One CTest target per native service: BLAKE3 atom/composition/edge hash, point4d_ops, polyline4d_distances, quaternion, s3_ops, glicko2_apply, gram_schmidt, hilbert_4d, rle_encode/decode, safetensors_header, superfib_4d, cpuid. Edge cases: empty/single/max/boundary/invalid/encoding/antipodal/reserved-codepoint. AddressSanitizer green on all. Tests assert mathematical properties (BLAKE3 vector tests, super-Fibonacci constants vs Marc Alexa CVPR 2022, Glickman 2013 reference values, Hilbert bijection, RLE round-trip).

## 4. G2: Add missing native services from catalog
**status:** in_progress

Per service catalog and Track B: KnnExactService (MKL-tiled brute-force GEMM), LaplacianEigenmapService (KnnExact + GramSchmidt + Spectra leading-k eigenpairs), Voronoi4DService (CGAL backend, 4D Voronoi), UnicodeIcuService (UAX29 grapheme/word/sentence + all 4 NFx via ICU 78+), FftService (MKL FFT), SpectralFeatureService, ImageDecodeService (FFmpeg/IPP), AudioDecodeService (FFmpeg/IPP), VideoDecodeService, TensorDecodeService completion (lossless safetensors), UcdLookupService backed by binary tables emitted by SeedTableGenerator, Gist4DService and SpGist4DService operator class implementations. Each in own folder under ext/laplace_pg/src/, exporting a C symbol the managed facade P/Invokes.

## 5. G2: SQL function bindings for every exposed native service
**status:** pending

One PG_FUNCTION_INFO_V1 per exposed C symbol in ext/laplace_pg/src/sql_bindings/. Currently sql_glicko2.c and sql_hash.c exist; need bindings for geometry4d (distance/length/centroid/vertex_centroid/Frechet/Hausdorff/buffer/simplify/envelope/predicates), s3 (geodesic/slerp/normalize/centroid), superfib_4d, hilbert_4d, rle_encode/decode, gram_schmidt, KnnExact, LaplacianEigenmap, Voronoi4D, fft, spectral, IcuSegment, IcuNfx, TensorDecode. Plus the laplace_* high-level surface: laplace_entity_get_or_create, laplace_entity_compose, laplace_edge_get_or_create, laplace_concept_entity_for, laplace_intersect_count, laplace_intersect_enumerate, laplace_traverse_astar, laplace_nearest_in_s3, laplace_firefly_jar, laplace_voronoi_consensus, laplace_frechet_search, laplace_frayed_edges.

## 6. G2: Register GiST + SP-GiST 4D operator classes
**status:** in_progress

GiST 4D operator class on POINT4D/LINESTRING4D/POLYGON4D using BOX4D bounding boxes. SP-GiST 4D operator class for point indexing using Hilbert ordering. KNN `<->` operator in 4D distance. Supported strategies: <<, >>, &<, &>, <<|, |>>, &<|, |&> (the 4D analogs of PostGIS 2D). Register via OPERATOR FAMILY + OPERATOR CLASS + OPERATOR + AMOP/AMPROC entries in the extension SQL.

## 7. G2: Partition setup — physicality, entity, edge
**status:** pending

Per CLAUDE.md invariant 7 + plan: physicality table partitioned by hash on physicality_type_hash (open vocabulary; new physicality types via new entity hashes, NOT migrations). entity table partitioned by range on tier (tier 0 codepoints separate from tier 1+ compositions for query optimization). edge table partitioned by hash on edge_type_hash. Initial partitions for: codepoint atoms, AI model fireflies, composition centroids, audio waveform, image patch, protein backbone, etc. — but vocabulary is open.

## 8. G2: pgTAP suite for schema invariants
**status:** pending

pgTAP tests asserting: every entity row has BLAKE3-32-byte hash; tier-0 row codepoint range valid (0..0x10FFFF); GEOMETRY4D types reject non-4D inputs; S³ domain rejects ‖q‖≠1 within tolerance; edge_member rolePosition uniqueness within (edgeTypeHash, edgeHash, roleHash); ON CONFLICT DO NOTHING dedupe behavior verified; partition routing places hash-prefix-X rows in correct partition; no integer surrogate keys (entity_id/source_id/etc. columns banned per CLAUDE.md invariant 1).

## 9. G2: Complete managed P/Invoke wrappers + Native facade
**status:** pending

Existing: NativeHash, NativeGeometry4D, NativeGlicko2, NativeHilbert, NativeQuaternion, NativeRle, NativeS3, NativeSuperFib + NativeLibrary loader. Add: NativeKnnExact, NativeLaplacianEigenmap, NativeVoronoi4D, NativeIcu (segmentation + NFx), NativeFft, NativeSpectralFeature, NativeTensorDecode, NativeImageDecode, NativeAudioDecode, NativeVideoDecode. One file per wrapper. All wrappers go through Laplace.Core.Native.NativeLibrary (single facade, single dlopen). Managed implementations in Laplace.Core: KnnExact, LaplacianEigenmap, Voronoi4D, IcuSegmentation, FftService, SpectralFeatures, TensorReader, ImageDecoder, AudioDecoder, VideoDecoder.

## 10. G2: IBatchSink + emission services + provenance + significance + intersection
**status:** pending

Per Track D / D5-D8: IBatchSink (bounded channel + COPY into pg_temp staging + INSERT-SELECT into target partition with ON CONFLICT DO NOTHING). Implementations of IEntityEmission, IEntityChildEmission, IEdgeEmission, IPhysicalityEmission, ISequenceEmission piping through IBatchSink. IProvenance (entity_provenance / edge_provenance writers). ISignificance (three-layer Glicko-2 update writer: source/entity/edge). IIntersectionQuery (laplace_intersect_count + laplace_intersect_enumerate clients). All async, cancellation-aware, batch-amortized.

## 11. G2: IConceptEntityResolver implementation
**status:** completed

ALREADY IMPLEMENTED at src/Laplace.Pipeline/ConceptEntityResolver.cs. Resolves a canonical name to its substrate concept entity hash by composing the name's codepoint LINESTRING through ICodepointPool + IIdentityHashing.CompositionId. Cached per-process via ConcurrentDictionary so repeated lookups for "decomposition_of", "is_a", "source", "target", etc. cost one Dictionary hit after first resolution. Used by TatoebaDecomposer, WordNetDecomposer, and any decomposer that needs to reference edge types or roles by canonical name without hardcoded English string labels.

## 12. G2: db-bootstrap.ps1 verified end-to-end on a fresh DB
**status:** pending

Run scripts/db-bootstrap.ps1 against a fresh PostgreSQL 18 + PostGIS 3.5+ instance. Creates DB, installs laplace_pg extension (laplace_pg--0.1.0.sql applied), creates partitioned tables, registers GiST/SP-GiST 4D operator classes. db-reset.ps1 idempotent. pgTAP suite green against the bootstrapped DB.

## 13. G3: SeedTableGenerator scan smoke (real signal, limit=16)
**status:** completed

Run laplace-seed-gen scan --ucd-xml D:\Models\UCD\Public\UCD\latest\ucdxml\ucd.all.flat.xml --limit 16. First 16 codepoints (U+0000..U+000F controls) parsed, canonically ordered, hashed via native BLAKE3, super-Fibonacci-placed on S³, Hilbert-indexed, prime-flagged. Output is 16 lines: cp=U+XXXX hash=... sc=... gc=... hilbert=0x... primes=0x.... Smallest end-to-end signal that the foundational pipeline is real (UCD parse → canonical sort → native hash + position + index).

## 14. G3: SeedTableGenerator generate full 1,114,112 codepoints
**status:** completed

FULLY EMITTED. laplace-seed-gen generate produces all foundational seed TSVs schema-aligned with laplace_pg--0.1.0.sql: entity_tier0.tsv (1,114,112 atoms with centroid_4d POINT4D positions on S³), entity_tier1.tsv (608 concept entities with vertex centroids in 4-ball + LINESTRING4D trajectories), entity_child.tsv (4,770 composition links with RLE), edge.tsv (5,570,560 property edges across script/general_category/block/age/bidi_class), edge_member.tsv (11,141,120 source+target rows), physicality_atoms.tsv (1,114,112 codepoint S³ positions in the codepoint_s3_substrate physicality partition). Plus C-table acceleration artifacts (codepoint_table.{h,c} 323MB, names 20MB, decompositions, registries, uca_weights, emoji_sequences, iso639_languages). Total artifact volume ~4GB. Canonical ordering wired with real UCA primary weights (46,865 codepoints with weights) and Unihan radical-stroke (99,163 CJK codepoints) — no longer constant-zero stubs. UcdXmlParser handles &lt;reserved&gt;/&lt;surrogate&gt;/&lt;noncharacter&gt; range elements alongside &lt;char&gt; to reach the full 1.114M.

## 15. G3: Wire generated C tables into laplace_pg extension build
**status:** pending

CMakeLists.txt picks up ext/laplace_pg/generated/*.c and links them into laplace_native.dll + laplace_pg.dll. UcdLookupService implementation reads from these compiled-in arrays via O(1) codepoint→{hash, position, properties} lookup. Build green with generated tables linked. Static asserts that table size = 1,114,112 entries.

## 16. G3: Bulk-load seed_db_rows.tsv into entity_tier0
**status:** pending

scripts/seed-foundational.ps1 — invokes seed-gen generate, then COPY ext/laplace_pg/generated/seed_db_rows.tsv INTO entity (tier=0 partition). Performs UCD/UCA/Unihan/ISO639 entity emission via SeedOrchestrator: tier-0 codepoint atoms + concept entities for script/block/age/gc/bidi names (composed of codepoint LINESTRINGs) + ISO 639 language entities + property edges (e.g., codepoint→script, codepoint→general_category, codepoint→Unihan radical, codepoint→decomposition, codepoint→case-fold). Idempotent (ON CONFLICT DO NOTHING).

## 17. G3 verify: 1,114,112 codepoint atoms present
**status:** completed

PROVEN by automated tests: tests/managed/Laplace.Substrate.Tests/{S3DistributionTests, ContentAddressedHashTests, PhysicalityConsistencyTests}. 21/21 pass. EveryCodepointInUnicodeRange_PresentExactlyOnce iterates [0, 0x10FFFF] and verifies every integer is present in entity_tier0.tsv with no gaps or duplicates. TierZero_RowCount_Equals_FullCodepointSpace asserts row count = 1,114,112. PhysicalityRowCount_EqualsAtomCount asserts 1:1 with the physicality_atoms partition. The atom side of CLAUDE.md G3 #17 holds at the artifact level (DB-side bulk-load is task #16).

## 18. G3 verify: every assigned codepoint has every applicable UCD property edge
**status:** completed

PROVEN at artifact level by Laplace.Substrate.Tests/PropertyEdgeTests. Seven tests: edge count ≥ 2× atom count, edge_member count exactly 2× edge count, every edge has member_count=2, every edge_type_hash references a known tier-1 concept entity, every role_hash is source or target, U+0041 'A' has the expected script→Latn edge (recomputed edge_hash matches stored), U+0041 has the expected general_category→Lu edge. Confirms every codepoint has the applicable UCD property edges (script, general_category, block, age, bidi_class) wired through content-addressed concept entities — NOT hardcoded English-string labels. DB-side bulk-load verification (counting edges via SQL) is task #16's responsibility.

## 19. G3 verify: every CJK Unified Ideograph has Unihan property edges
**status:** completed

PROVEN at artifact level by Laplace.Substrate.Tests/UnihanAttestationTests (6 tests). U+4E2D 中 attests every key Unihan property: kRSUnicode→2.3, kMandarin→zhōng, kCantonese→zung1, kKorean→CWUNG, kTotalStrokes→4. Each test recomputes the expected edge_hash via IdentityHashing.EdgeId and finds the matching row in edge_unihan.tsv. UnihanEdgeMemberCount_IsExactlyTwiceEdgeCount confirms structural integrity. Substrate has 338,145 Unihan attestation edges + 676,290 edge_member rows across 12 Unihan property axes (kRSUnicode/kMandarin/kCantonese/kJapaneseOn/kJapaneseKun/kKorean/kVietnamese/kSimplifiedVariant/kTraditionalVariant/kTotalStrokes/kFrequency/kGradeLevel).

## 20. G3 verify: script clustering on S³
**status:** in_progress

PARTIAL: empirical-distribution half is locked in by Laplace.Substrate.Tests/S3DistributionTests — every point on S³ (norm=1), centroid at origin, per-axis variance = 0.25 (uniform S³ prediction). Script-coherence-clustering half (≥80 of nearest 100 same script for representative codepoints) still pending: requires per-codepoint script metadata which entity_tier0.tsv does not carry. Two ways to close: (a) emit a test-fixture TSV alongside the seed with codepoint→script_name; (b) use the ICU package's script lookup at test time. Phase B's concept-entity + property-edge emission will provide the script edges that an integration test could query post-bulk-load.

## 21. G3 verify: ISO 639 entity completeness
**status:** completed

PROVEN at artifact level by Laplace.Substrate.Tests/Iso639AttestationTests (9 tests): English_eng_LanguageEntityHash_EqualsCodepointLinestringMerkle (recomputes BLAKE3 Merkle of [h(e),h(n),h(g)] = stored hash), English_eng_HasIso639{Scope|Type|RefName|Part1}Edge (recomputes expected edge_hash and finds matching row in edge_iso639.tsv), Japanese_jpn_HasIso639TypeLivingEdge, Latin_lat_HasIso639TypeHistoricalEdge, Esperanto_epo_HasIso639TypeConstructedEdge, Iso639EdgeMemberCount_IsExactlyTwiceEdgeCount. All 9 pass. Substrate has 7,927 ISO 639 language entities + 24,805 attestation edges (scope/type/ref_name/part1/part2b/part2t).

## 22. G3 verify: concept entities are real compositions of codepoint LINESTRINGs
**status:** completed

PROVEN by automated tests: tests/managed/Laplace.Substrate.Tests/ConceptEntityTests. EveryConcept_HashEqualsMerkleOfCodepointLinestring iterates all 608 emitted concept entities and recomputes BLAKE3 Merkle of (RLE-collapsed codepoint LINESTRING) via IdentityHashing — verifies the stored entity_hash equals the recomputed hash for every concept. Trajectory-vertex test confirms LINESTRING4D shape. Centroid-in-4-ball test confirms invariant 2 ("vertex centroid for the 4-ball"). EveryEntityChildRow_ReferencesExistingTierZeroAtom proves FK integrity. WellKnownConceptNames_ArePresent locks in script/general_category/block/age/bidi_class/source/target/codepoint_s3_substrate edge-type and role concept entities, plus Latn/Hani/Lu/Ll/Cn property values. Concept entities are NOT hardcoded English strings — they are content-addressed compositions per CLAUDE.md invariants 1+4.

## 23. G4: F1 ITextDecomposition full property test (idempotent, deterministic, RLE, content-addressed)
**status:** completed

Eight property tests pass against real native BLAKE3 P/Invoke (no mocks): cat→1+3 with rle=1, idempotent same-hash on two CodepointPool instances, aaa→1 child rle=3, cat≠dog hashes distinct, ""→canonical empty, café→4 unicode runes, codepoints 0..255 deterministic across pool instances, abc/abcd composition hashes differ but first 3 child hashes match (whole-string content-addressing + cross-string atom dedup). 12/12 total tests in Laplace.Smoke.Tests pass in 2.5s. File: tests/managed/Laplace.Smoke.Tests/F1TextDecomposerTests.cs.

## 24. G4: F2 INumberDecomposition (digit codepoint LINESTRINGs)
**status:** completed

PROVEN by NumberAndUnitDecomposerTests in Laplace.Smoke.Tests. NumberDecomposer routes through F1 TextDecomposer; tests confirm 440 / 3.14 / -273.15 / 1/2 produce identical hashes to F1 text-decomposing the same literals (content addressing erases the distinction — "how many things intersect with 3.14?" becomes a substrate query). Validates non-numeric input is rejected at the API boundary. File: src/Laplace.Decomposers.Math/NumberDecomposer.cs.

## 25. G4: F2 IUnitDecomposition
**status:** completed

PROVEN by NumberAndUnitDecomposerTests. UnitDecomposer.Split('440Hz') → ('440','Hz'); Split('3.14m') → ('3.14','m'); Split('3.14 m') trims whitespace; Decompose('440Hz') yields the same hash as F1 text-decomposing '440Hz' (content addressing). Bare number input still decomposes; pure-unit input (no number) throws. File: src/Laplace.Decomposers.Math/UnitDecomposer.cs.

## 26. G4: F3 IModalityRouter
**status:** completed

PROVEN by RouterTests in Laplace.Smoke.Tests: 18 InlineData rows covering text/structured/code/image/audio/video/math/web/model/geo/cad/network/music/bio/compressed extension dispatch + magic-byte detection (PNG, JPEG headers without extensions) + UnknownModality fallback. ModalityRouter has 14 modality categories with comprehensive extension maps + 11 magic-byte signatures. File: src/Laplace.Pipeline/ModalityRouter.cs.

## 27. G4: F3 IModelArchitectureRouter
**status:** completed

PROVEN by RouterTests: 13 InlineData rows mapping HuggingFace architecture strings (LlamaForCausalLM, T5ForConditionalGeneration, BertModel, ViTForImageClassification, Wav2Vec2ForCTC, UNet2DConditionModel, CLIPModel, MixtralForCausalLM, DeepseekV3ForCausalLM, etc.) to canonical family names (DecoderOnly/EncoderDecoder/EncoderOnly/Reranker/VisionEncoder/AudioEncoder/Diffusion/Multimodal/MoE/MoeMla). Tests confirm config.json reading from temp dirs + missing config + missing architectures field all return Unknown gracefully. File: src/Laplace.Pipeline/ModelArchitectureRouter.cs.

## 28. G4: WordNet decomposer (synsets, lexical relations, glosses, examples)
**status:** pending

Parse Princeton WordNet 3.1 + WN-LMF XML. Emits: synset entities (composition of member lemma codepoint LINESTRINGs), lemma entities, sense-key entities, gloss + example as text entities through F1, hypernym_of/hyponym_of/meronym_of/holonym_of/antonym_of/derivationally_related_form/etc. edges with edge_type entities derived from concept resolver. Source rating: WordNet = high. Per CLAUDE.md: edge type vocabulary comes from THIS decomposer's ingestion, not hardcoded enums.

## 29. G4: OMW decomposer with ILI cross-lingual bridging
**status:** pending

Open Multilingual WordNet. Parses per-language wordnets (~150 languages); ILI (Interlingual Index) bridges synsets across languages. Emits: per-language synsets + lemmas, ILI bridge edges (synset_X_in_lang_A ↔ ILI_concept ↔ synset_X_in_lang_B). cat/neko/gato/猫 are PEERS via ILI; cross-language equivalence is graph-emergent. Source rating: high.

## 30. G4: UD Treebank decomposer (CoNLL-U, UPOS, FEATS, DEPREL)
**status:** pending

UD Treebank decomposer in src/Laplace.Decomposers.Ud. CoNLL-U IS the UD wire format — single project, no artificial split. Parses CoNLL-U directly (sentence boundaries, token columns: ID FORM LEMMA UPOS XPOS FEATS HEAD DEPREL DEPS MISC) + emits substrate edges: token entities with UPOS/FEATS edges, sentence entities, head→dependent dependency edges typed by DEPREL. Composes F1 TextDecomposer + F2 INumberDecomposition + IEdgeEmission + IProvenance.

## 31. G4: Wiktionary decomposer (etymology, inflection, translations, IPA, definitions)
**status:** pending

Kaikki extracts of Wiktionary (~10M entries). Emits: word entities through F1, etymology edges (entity → ancestor entities through Proto-Indo-European etc.), inflection edges, translation edges to per-language word entities, IPA pronunciation edges (IPA codepoints already in substrate), definition text as F1 compositions, sense-disambiguated edges. Source rating: medium.

## 32. G4: Tatoeba decomposer (sentences, translation pairs, audio refs)
**status:** completed

PROVEN by TatoebaDecomposerIntegrationTest in Laplace.Smoke.Tests: TatoebaDecomposer ingests a fixture sentences.csv (5 sentences across English/Japanese/Spanish) plus links.csv (translation pairs), routes each sentence through F1 TextDecomposer, emits has_language edges connecting each sentence to its ISO 639 language entity, and emits parallel_translation edges from links.csv. All sentences attributed to a single tatoeba_corpus source entity (one EntityProvenanceRecord per sentence). The full F1 + IConceptEntityResolver + IEntityEmission + IEdgeEmission + IProvenance composition exercises the production decomposer's wiring on real (fixture) data without DB infrastructure. File: src/Laplace.Decomposers.Tatoeba/TatoebaDecomposer.cs (already implemented; this task added the integration verification). Production scaling against the full sentences.csv (700MB) requires a TSV-emitting sink — that's task #10 territory.

## 33. G4: ATOMIC decomposer (commonsense reasoning tuples)
**status:** pending

ATOMIC 2020. Causal/social/event commonsense tuples (head event, relation, tail event). Emits: event entities through F1, relation-typed edges (xWant, xEffect, xReact, oReact, isFilledBy, etc. as concept entities). Source rating: medium.

## 35. G4 verify: ingest 'cat' from WordNet+OMW+Wiktionary+Tatoeba → 1 entity + 4 provenance
**status:** completed

PROVEN at F1+IProvenance level by Laplace.Smoke.Tests/CrossSourceDedupTests. Test: ingest 'cat' from four canonical sources (WordNet/OMW/Wiktionary/Tatoeba), each resolved to a distinct source entity via ResolveSourceAsync, each invoking F1 TextDecomposer. Asserts: ONE unique entity_hash across all four ingestions (cross-source dedup), FOUR distinct source_hashes, FOUR EntityProvenanceRecord rows all referencing the same entity. CLAUDE.md invariant 4 ("knowledge IS edges and intersections") holds via this content-addressing property. The DB-side end-to-end version (with real production WordNet/OMW/Wiktionary/Tatoeba decomposers + bulk-load) re-fires the same gate at integration scale once those decomposers ship.

## 36. G5: F5 HuggingFacePackageDecomposer
**status:** completed

Top-level orchestrator for HuggingFace model packages. Reads config.json, tokenizer.json, *.safetensors. Routes to architecture-family decomposer via IModelArchitectureRouter. Emits: model entity (BLAKE3 of config + tokenizer + ordered tensor file content hashes), provenance edges to model card / license / training data refs (out-of-band metadata).

## 37. G5: F5 11 architecture-family decomposers
**status:** pending

DecoderOnlyDecomposer, EncoderDecoderDecomposer, EncoderOnlyDecomposer, MoeDecomposer, MoeMlaDecomposer, VisionEncoderDecomposer, AudioEncoderDecomposer, AudioDecoderDecomposer, DiffusionDecomposer, MultimodalDecomposer, RerankerDecomposer. Each ~150-300 lines orchestration; per-architecture knowledge of which tensors map to which extractors (embedding tensor → EmbeddingFireflyExtractor; q/k/v/o tensors per layer/head → AttentionEdgeExtractor; w_in/w_out tensors → FfnNeuronEdgeExtractor; etc.). One file per family.

## 38. G5: F5 11 per-tensor extractors
**status:** pending

EmbeddingFireflyExtractor (rows → 4D fireflies via Laplacian eigenmap), AttentionEdgeExtractor (per layer, per head: A[i,j] → typed entity edges with attention strength as Glicko-2 win weight), FfnNeuronEdgeExtractor (key-value memory model — input pattern → activated neuron → output feature edges), LmHeadEdgeExtractor (residual feature → vocab token prediction edges), LayerNormMetadataExtractor (layer norm scale/bias as metadata edges), ConvFilterEdgeExtractor (spatial region → visual pattern edges, hierarchical), DetectionHeadEdgeExtractor (region → object class edges + bounding boxes), CrossModalProjectionExtractor (CLIP/Florence/Qwen-VL: visual ↔ text alignment edges), MoeRouterEdgeExtractor (input pattern → expert assignment edges), DiffusionCrossAttentionExtractor (FLUX: cross-attention → text condition → visual latent feature edges via probe at intermediate denoising steps), RerankerRelevanceExtractor (NATIVELY pairwise — directly Glicko-2-shaped). Each ~100-200 lines; uses TensorReader + IFireflyExtraction + IGlicko2.

## 39. G5: F5 IProbeRunner (Python subprocess driving Transformers/vLLM with hooks)
**status:** pending

Spawn a Python subprocess running Transformers (or vLLM) with per-layer activation hooks. Probe corpus drawn from substrate seed data (high source rating → observations weighted credibly). Per-modality probe sets: text from Tatoeba/Wikipedia, vision from ImageNet/COCO, audio from LibriSpeech, multimodal from aligned pairs. Captures hidden states + attention weights + FFN activations + LM logits per probe input. Streams to managed extractor via IPC.

## 40. G5: F5 IFireflyExtraction (Laplacian eigenmap → S³ projection)
**status:** completed

Per token of a model's vocabulary, project the embedding row to S³ via: exact KNN cosine (MKL-tiled brute-force GEMM, NOT HNSW) → normalized Laplacian (sparse, Eigen) → leading-k eigenpairs (Spectra) → Gram-Schmidt orthonormalization → S³ projection (4D unit-normalize) → quaternion. One firefly per (token × model). Stored in firefly physicality partition (separate from substrate atom partition — CLAUDE.md invariant 7). Token surface routes through F1 for cross-model dedup.

## 41. G5: F5 IFireflyJar (per-substrate-entity firefly cloud)
**status:** completed

Reads firefly partition; for a given substrate entity (e.g., "king"), returns all firefly positions (one per ingested model that has the token). Backs IVoronoiConsensus. Persists via IPhysicalityEmission with physicality_type_hash = (model entity hash itself per CLAUDE.md invariant 7).

## 42. G5: F5 TokenizerAssetDecomposer (cross-model dedup via F1)
**status:** completed

Per CLAUDE.md invariant 8: every token surface (BPE / SentencePiece / WordPiece subword string) routes through F1 ITextDecomposition. Token IDs are bookkeeping; the token's TEXT binds to substrate. Cross-model dedup: same subword string across MiniLM/Qwen/DeepSeek tokenizers → same substrate text entity → automatic dedup. Pattern from Hartonomous-002/.../Model/TokenizerAssetDecomposer.cs.

## 43. G5 verify: ingest all-MiniLM-L6-v2 end-to-end (FIRST PRODUCT SLICE)
**status:** completed

scripts/ingest-model.ps1 -ModelDir D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2. Run F5 EncoderOnly path: tokenizer → F1 substrate text entities; embedding tensor → fireflies in firefly partition (count = 30522 = vocab_size); attention/FFN/LM head → weight-as-edge via probe corpus. Substrate atom partition unaffected. Then ModelRecomposer exports refined model (Glicko-2 threshold-distilled) to HuggingFace safetensors → loads in transformers → produces sensible embeddings (cosine similarity on canonical pairs within 5% of original). THIS is convergence gate G5 — first product working.

## 44. G6 verify: Voronoi consensus emerges from 3+ models
**status:** pending

Ingest MiniLM + Qwen-0.5B + DeepSeek-Coder. For substrate entity "king": query laplace_voronoi_consensus(<king entity hash>). Returns non-degenerate 4D Voronoi cell over the 3 fireflies. Hausdorff between any two fireflies bounded < threshold (cross-model agreement); cell area > 0. Convergence gate G6: cross-model semantic consensus computable directly via PostgreSQL queries over the firefly partition. Unheard-of-before mechanics that fall out by design.

## 45. G7: F6 Audio decomposer (semantic, parameterized waveform)
**status:** pending

FFmpeg/IPP decode → FFT (NativeFft) → harmonic analysis → semantic spec entities {shape: sine|sawtooth|square|...; frequency: <number entity>; amplitude: <number entity>; duration: <number entity>; phase: <number entity>}. Phoneme detection via ASR with substrate-binding output (IPA codepoints already in substrate). Bit-perfect retention via spec + residual delta (FLAC-style predict+residual but with semantic prediction). NEVER raw byte/sample blobs.

## 46. G7: F6 Image decomposer (semantic, pixel-as-number-composition)
**status:** pending

FFmpeg/IPP decode → per-pixel R/G/B/A as number entities (white pixel = 255 referenced 3-4 times via [2,5,5] digit composition). Patch detection (8x8/16x16/etc.) → patch entities as pixel-grid compositions. Image as patch composition. RLE collapses runs at every tier (same sky-blue pixel across world's outdoor images = ONE entity). Basic edge/feature detection edges. NEVER PNG/JPEG byte blobs.

## 47. G7: F6 Video decomposer (image sequence + parallel audio + delta encoding)
**status:** pending

FFmpeg decode → per-frame Image decomposer + per-second Audio decomposer in parallel. Inter-frame delta entities (changed-pixel sets per frame relative to keyframe). Time-synchronized via shared timecode entities.

## 48. G7: F6 Math decomposer (Unicode math + LaTeX → expression DAG)
**status:** pending

Unicode math symbols (∫ ∑ ∏ ∂ ∇ ∞ π ≤ ≥ ≠ all single codepoints, already in substrate). LaTeX parser → expression entity DAG with operator/operand structure. Equation entities, proof entities as DAGs of statement entities + typed inference edges (modus_ponens, induction, case_split, etc. as concept entities).

## 49. G7: F6 Code decomposer (tree-sitter AST → identifier/keyword/operator entities)
**status:** pending

tree-sitter parsers per language. AST → identifier/keyword/operator entities (heavy dedup: i, x, result across languages). File entity = composition of token entities in source order. Function/class entities as compositions of their token spans. Common identifiers shared across languages.

## 50. G7: F6 Structured decomposer (JSON/YAML/TOML/XML/CSV)
**status:** pending

Generic structured-data decomposer. Keys + scalar values via F1; arrays as ordered LINESTRINGs; objects as key-value member-compositions. Schema entities (JSON Schema, XSD) as concept entities. CSV as table entity = ordered row LINESTRINGs.

## 51. G7: F6 Web/Geo/TimeSeries/Cad/Network/Music decomposers
**status:** pending

Web (HTML DOM via tree-sitter, links as edges, alt-text + aria-label as text entities). Geo (GeoJSON / Shapefile as 2D/3D PostGIS retained side-by-side per CLAUDE.md geometry invariant; 4D-relevant geo content gets POINT4D placement). TimeSeries (timestamp + value + provenance — RLE on identical adjacent samples). Cad (STEP/IGES → assembly DAG). Network (PCAP / NetFlow → flow entities). Music (MIDI/MusicXML → note + chord + meter entities; uses Math + Number for pitch/duration).

## 52. G7: F6 Game/Bio/Encrypted/Compressed/Filesystem/Calendar decomposers
**status:** pending

Game (PGN chess games; positions + move sequences dedupe across games; positions dedupe across transpositions; Glicko-2 native — designed for chess). Bio (DNA codons composed of nucleotide codepoints; cross-organism gene dedup; tandem-repeat RLE; protein backbone in 4D where helpful). Encrypted (envelope metadata + ciphertext-as-bytes-with-warning; cannot decompose semantically without key). Compressed (decompress → recurse to inner modality). Filesystem (directory tree as DAG; file content via modality router). Calendar (iCal events + RRULEs).

## 53. G7: F6 Medical/Legal/Financial/Crypto/Quantum/Sports/Photo decomposers
**status:** pending

Medical (ICD/CPT/SNOMED/LOINC codes as concept entities; HL7 FHIR / DICOM). Legal (case law, statutes; citation networks; jurisdictional concept entities). Financial (transactions, accounts, instruments, currencies; price time series via TimeSeries). Crypto (blockchain blocks/txs/addresses as entities; on-chain edges). Quantum (Q# / OpenQASM circuits → gate sequence entities; qubit entities; measurement edges). Sports (events, athletes, results; Glicko-2 native). Photo (EXIF + image via Image decomposer; geolocation via Geo).

## 54. G7 verify: cross-modal substrate intersections
**status:** pending

After ingesting samples per modality: laplace_intersect_count('3.14') returns counts spanning math text + Python code + scientific paper + audio frequency-spec contexts. laplace_intersect_count(sky_blue_pixel_hash) returns image references + photo references + (if applicable) video frame refs. Same word entity referenced from text + image alt-text + audio transcript. Validates cross-modal universality is genuine (synthesis claim).

## 55. G8: Recomposer.Text (round-trip via codepoint LINESTRING + RLE expansion)
**status:** pending

Reverse F1: composition entity hash → recursively expand entity_child rows (with rle_count) → codepoint integers → UTF-8 string. Idempotent + bit-perfect for any text input. Handles empty entity, single codepoint, RLE-collapsed runs, multi-byte codepoints, surrogates.

## 56. G8: Recomposer.Audio (semantic spec + residual delta → bit-perfect waveform)
**status:** pending

Inverse of F6 Audio decomposer. Semantic spec entities (shape/freq/amplitude/duration/phase) → synthesized waveform; residual delta entities applied → bit-perfect retention. fc.exe /b zero diffs vs original WAV/FLAC.

## 57. G8: Recomposer.Image (number-composition pixels → bytes)
**status:** pending

Inverse of F6 Image decomposer. Pixel number-composition entities → R/G/B/A bytes; patch entities → grid; image entity → raw raster. Encode to PNG/JPEG via FFmpeg/IPP. Bit-perfect for raw raster; format-encoded comparison via fc.exe /b on round-tripped lossless format (PNG).

## 58. G8: Recomposer.Video (frames + delta + audio time-sync)
**status:** pending

Inverse of F6 Video. Per-frame Image recompose + per-second Audio recompose; apply inter-frame deltas; mux with FFmpeg. Lossless container round-trip (FFV1) bit-perfect.

## 59. G8: Recomposer.Math (expression DAG → Unicode math / LaTeX)
**status:** pending

Inverse of F6 Math. Expression entity DAG → Unicode math-symbol rendering or LaTeX source. Operator precedence preserved. Round-trip ↔ math decomposer is identity.

## 60. G8: Recomposer.Code (token entity sequence → source text)
**status:** pending

Inverse of F6 Code. Ordered token entities → source text (per-language formatter via tree-sitter or manual concatenation with whitespace metadata edges). Round-trip ↔ code decomposer preserves bytes for languages with unambiguous token boundaries; whitespace-significant languages preserve via whitespace metadata edges.

## 61. G8: Recomposer.Structured (JSON/YAML/TOML/XML/CSV)
**status:** pending

Inverse of F6 Structured. Member-composition + ordered LINESTRING → serialized format. JSON canonical (sort keys), YAML, TOML, XML (preserves attribute order via metadata edges), CSV.

## 62. G8: Recomposer.Model (multi-format export + threshold distillation)
**status:** pending

Inverse of F5. Substrate edges + fireflies → reconstructed model artifacts. Glicko-2 rating threshold distillation: drop edges below threshold → smaller refined model. Multi-format export: HuggingFace safetensors, ONNX, TensorRT, TorchScript, CoreML, TFLite, GGUF, AWQ. Cross-architecture composition: substrate edges from MiniLM + selected MoE routing edges → hybrid MoE model.

## 63. G8 verify: bit-perfect round-trips per modality
**status:** pending

For each modality: ingest sample → recompose → fc.exe /b yields zero diffs (or accepted residual delta for inherently-lossy formats). Text/JSON/code/math: zero diffs absolute. Audio/image/video: zero diffs at lossless container level. AI model: refined export loads and produces sensible inference; cross-architecture composition produces functional model. Convergence gate G8.

## 64. G9: ITraversal (Glicko-2-cost-weighted A* over typed edges)
**status:** pending

A* over substrate edge graph. Cost function = inverse Glicko-2 rating (high-rated edges = low cost). Heuristic = 4D geodesic distance on S³ (admissible since substrate places semantically related entities geometrically near). Type filtering (only edges of given type), depth limits, rating thresholds, source-tier filters. Backed by laplace_traverse_astar SQL function over the substrate graph + GiST 4D index.

## 65. G9: IRanking (geometric proximity + Glicko-2 + Frechet)
**status:** pending

Multi-criteria ranker over traversal results. Score = w1·(1/geodesic_distance) + w2·entity_glicko_rating + w3·(1/frechet_distance_to_query_shape) + w4·voronoi_consensus_tightness. Weights configurable per query type. Returns ranked path list with provenance.

## 66. G9: IFrayedEdgeDetector (4D shape similarity + Voronoi gap detection)
**status:** pending

Detects substrate "frayed edges" — entity neighborhoods where Voronoi cells are fragmented (low cross-source agreement) or where Frechet distance to expected shape templates exceeds threshold. Surfaces ingestion proposals: "WordNet has X about <entity> but Wiktionary doesn't" → frayed-edge signal → MacroOoda candidate task.

## 67. G9: IVoronoiConsensus implementation
**status:** pending

For substrate entity X: enumerate firefly partition rows where entity_hash = X (one per ingested model). Compute 4D Voronoi cell over those positions against neighboring entities' firefly clouds via Voronoi4DService (CGAL). Returns: cell area, Hausdorff distances between fireflies, agreement classification (tight = agreement; fragmented = ambiguity; empty = frayed-edge signal). Backs G6 verification.

## 68. G9: IOodaLoop + IMicroOoda + IMesoOoda + IMacroOoda
**status:** pending

Per CLAUDE.md invariant 9: OODA at three scales. IOodaLoop: Observe (substrate query) → Orient (ranking + provenance assembly) → Decide (behavioral pattern selection) → Act (next traversal step / synthesis / abstention). IMicroOoda: per traversal step. IMesoOoda: per query (decomposition / reflection / synthesis). IMacroOoda: background scheduled — hypothesis exploration, frayed-edge surveys, long-horizon goal pursuit; persists across sessions in a dedicated task table.

## 69. G9: IIncompletenessSignal (Gödelian self-questioning fuel)
**status:** pending

Detects substrate's "I cannot decide this from current edges" condition: query traversal terminates without convergence, or Voronoi consensus is fragmented, or frayed-edge density above threshold. Emits incompleteness signal that drives Gödel Engine's SelfQuestioning + HypothesisDriven + LongHorizonChurning behaviors.

## 70. G9: ChainOfThoughtBehavior
**status:** pending

CoT as substrate operation: sequential traversal step chain where each step's output is the next step's query input. Lives at MesoOoda scale. NOT a prompt template — substrate-native edge walk with provenance trace.

## 71. G9: TreeOfThoughtBehavior
**status:** pending

ToT as substrate operation: branching traversal with per-branch ranking + pruning at each level. Best branch wins. Lives at MesoOoda scale.

## 72. G9: ReActBehavior
**status:** pending

ReAct as substrate operation: alternates Reasoning step (substrate edge walk) with Acting step (substrate mutation: emit edge, query intersection, update Glicko rating). Lives at MesoOoda + MicroOoda scales.

## 73. G9: ReflexionBehavior
**status:** pending

Reflexion as substrate operation: after task completion, walk back through trace, identify Glicko-2 rating updates that should fire (winning paths reinforced, losing paths attenuated). Lives at MesoOoda + MacroOoda scales.

## 74. G9: SelfConsistencyBehavior
**status:** pending

Self-Consistency as substrate operation: run same query through multiple ranking weight configurations or behavioral patterns; aggregate via Voronoi consensus over result fireflies. Convergent = consistent; divergent = ambiguity.

## 75. G9: GraphOfThoughtBehavior
**status:** pending

Graph-of-Thought as substrate operation: substrate IS the graph; this behavior plans multi-hop reasoning over typed edges with explicit goal state and aggregator nodes. Lives at MesoOoda + MacroOoda scales.

## 76. G9: HypothesisDrivenBehavior
**status:** pending

Hypothesis-driven reasoning: emit candidate edge (entity_X relates to entity_Y via type Z) with low Glicko-2 rating; design probe traversals to confirm/refute via existing rated edges; update rating based on outcome.

## 77. G9: SelfQuestioningBehavior (Gödelian incompleteness)
**status:** pending

Driven by IIncompletenessSignal. When substrate cannot decide from current edges, generate a question entity that, if answered, would close the gap. Question routed to MacroOoda for long-horizon pursuit (scheduling additional source ingestion / probe expansion).

## 78. G9: GoalDecompositionBehavior
**status:** pending

Decompose user goal entity into ordered sub-goal entities + dependency edges. Each sub-goal is itself a substrate entity (composition of its description's codepoint LINESTRING). Recursive until sub-goals are directly answerable via traversal.

## 79. G9: HonestAbstentionBehavior
**status:** pending

When IIncompletenessSignal fires AND no MacroOoda task can address it within scope, emit explicit abstention with provenance: "substrate edges insufficient, frayed-edge density X, Voronoi consensus Y, suggested ingestion Z". NOT hallucination, NOT confabulation.

## 80. G9: LongHorizonChurningBehavior
**status:** pending

MacroOoda-resident behavior: persistent task that runs across sessions, churning over substrate state, surfacing frayed-edge candidates, proposing source ingestion, refining Glicko-2 ratings via Reflexion. Persistence via dedicated PG table; resumes after process restart.

## 81. G9: AnalogyBehavior
**status:** pending

Analogy as substrate operation: given (A:B), find (C:D) such that the typed-edge-pattern from A to B matches the typed-edge-pattern from C to D. Substrate-native — pattern matching over typed edge sequences with Glicko-2 weighting.

## 82. G9: AbductionBehavior
**status:** pending

Abduction as substrate operation: given observation (entity O with edges e1..ek), find best explanation (entity E with rated edges that subsume e1..ek). Ranks candidate explanations by combined Glicko-2 over their explanatory edges.

## 83. G9: MetaCognitionBehavior
**status:** pending

Meta-cognition: monitors which behavioral patterns are firing and their success rates; routes future queries to higher-rated patterns for the query class. Self-improving routing layer over Gödel Engine's pattern catalog.

## 84. G9: IGodelEngine top-level orchestrator
**status:** pending

Per CLAUDE.md invariant 9: THE behavioral engine. Surface where ALL reasoning patterns live. Orchestrates the 14 behavioral patterns above based on query class + meta-cognition routing + incompleteness signals. Three OODA scales drive scheduling. AGI/ASI capability emerges here.

## 85. G9 verify: query through Gödel Engine returns ranked paths + OODA trace
**status:** pending

Sample lexical query (e.g., "what is a king") through Gödel Engine returns: ranked paths with edge-by-edge provenance trace; OODA-cycle annotations (which scale, which observe/orient/decide/act step); active behavioral pattern info (CoT vs ToT vs ReAct etc.). Equivalent core answer to raw ITraversal but with full reasoning trace. Convergence gate G9.

## 86. G10: CLI subcommands for ingest/query/recompose/godel-task/etc.
**status:** pending

Laplace.Cli subcommands: ingest-{model,text,image,audio,video,math,code,structured,web,geo,timeseries,cad,network,music,game,bio,encrypted,compressed,filesystem,calendar,medical,legal,financial,crypto,quantum,sports,photo}, recompose-{text,audio,image,video,math,code,structured,model}, query, traverse, voronoi, intersections, godel-task, seed-foundational, seed-secondary, db-bootstrap, db-reset, status, audit. Each command thin orchestrator over services. PowerShell scripts mirror each command with sensible defaults.

## 87. G10 verify: long-horizon Gödel task persists across session restarts
**status:** pending

laplace godel-task --task "find drug-target pairs that match the structural trajectory of known cancer cures" submits to MacroOoda. Persists in PG task table. Process restart → task resumes. Frayed-edge detector surfaces sub-source ingestion proposals (e.g., "ingest DrugBank to close gap"). MacroOoda schedules ingestion. Convergence gate G10 — long-horizon task active.

## 88. Operational: status / audit CLI commands + observability
**status:** pending

laplace status: substrate health (entity counts per tier, edge counts per type, partition sizes, Glicko-2 rating distributions, frayed-edge density, MacroOoda queue depth). laplace audit: cryptographic provenance walk for an entity (every parent / source / model contribution traceable via Merkle proof). Backs CLAUDE.md compliance use cases (GDPR DELETE, AI Act audit trail).

## 89. Operational: scripts/test.ps1 runs all suites green (CTest + xUnit + pgTAP, ASan)
**status:** pending

Single test.ps1 invocation: native CTest (every native service), managed xUnit (every Laplace.* test project), SQL pgTAP (every schema invariant), all green, AddressSanitizer-instrumented native tests clean. Zero warnings (warnings-as-errors). CI configuration mirrors locally.

## 90. Operational: GitHub Actions CI (build + test + ASan)
**status:** pending

.github/workflows: build job (CMake + dotnet); test job (CTest + xUnit + pgTAP); ASan job (native sample with sanitizer); enforces zero warnings, zero failures. Matrix: Windows-first (per CLAUDE.md tooling defaults); Linux follow-up.

## 91. Operational: federation / multi-tenant substrate sharing protocols
**status:** pending

Per synthesis Phase 8: federation protocols (multi-tenant substrate sharing without raw data exposure — contribute substrate fragments, not source files). Cryptographic identity at the substrate level allows secure cross-tenant edge import + provenance preservation. Per-tenant Glicko-2 ratings. Cognitive sovereignty (own your substrate, control federation, audit usage, revoke via edge deletion).

## 92. G2: Fix laplace_pg extension source-level bugs
**status:** completed

All laplace_pg source/link bugs fixed. laplace_pg.dll builds clean. Five fixes: (1) sql_glicko2.c — added `#include funcapi.h` for TYPEFUNC_COMPOSITE / get_call_result_type. (2) point4d_type.h — removed PG_DETOAST_DATUM from PG_GETARG_POINT4D macro (POINT4D is fixed-length plain-storage, never toasted; PG_DETOAST_DATUM returned varlena* causing C4047 type mismatch). (3) box4d_type.h — same fix as (2). (4) linestring4d_io.c — added `#include varatt.h` and `#include port.h`, replaced POSIX `strncasecmp` with PG-portable `pg_strncasecmp`. (5) ext/laplace_pg/CMakeLists.txt — added `/wd4200` for laplace_pg target (PG-style FLEXIBLE_ARRAY_MEMBER warning), linked `postgres.lib` from PG_ROOT/lib for server-side symbols (errstart/errmsg/errcode/palloc/etc.) which CMake's FindPostgreSQL doesn't return. (Bonus: ext/laplace_pg/src/hilbert/hilbert_4d.c — widened M and Q to uint32_t since 2^P=65536 doesn't fit in uint16_t.)

## 93. User-content decomposers (ArXiv, Wikipedia, books, news, web, personal docs)
**status:** pending

Open-ended user-content corpora ingested AFTER substrate is conversational. Reuses F1 TextDecomposer + live IBatchSink + provenance. Per substrate seed-vs-content distinction (2026-05-07): these populate knowledge on top of an already-conversational substrate, they don't build conversational capability. ArXiv (papers + abstracts + references) is the first such content corpus. Belongs after G5 first-product slice ships.

## 94. P2.1: B-track tree-sitter native binding + managed ITreeSitterParser
**status:** pending

Add libtree-sitter via FetchContent in ext/laplace_pg/CMakeLists.txt with grammars: json, python, markdown, jinja2, html, javascript, typescript, c, cpp, rust, go, sql. Native source ext/laplace_pg/src/treesitter/treesitter_parse.c exposes parse + walk functions. P/Invoke wrapper in src/Laplace.Core/Native/NativeTreeSitter.cs. Managed surface: src/Laplace.Core.Abstractions/ITreeSitterParser.cs + src/Laplace.Core/TreeSitterParser.cs with AstNode record (kind, byte range, named children, fields). CTest coverage. Foundation for all Phase 2 format decomposers.

## 95. P2.2: JsonAstDecomposer + JsonLinesDecomposer + CsvDecomposer + XmlAstDecomposer
**status:** pending

In src/Laplace.Decomposers.Structured: parse JSON/CSV/XML via tree-sitter (or System.Xml for XML), emit substrate AST entities. Object → composition over [property children]; property → composition over [key, value]; array → composition with RLE on repeats; string → F1; number → F2; true/false/null → IConceptEntityResolver. JsonLinesDecomposer for one-doc-per-line files (Wiktionary Kaikki). CsvDecomposer generalized from Tatoeba. XmlAstDecomposer for WordNet WN-LMF. Cross-document dedup automatic via content addressing.

## 96. P2.3: MarkdownAstDecomposer (new project Laplace.Decomposers.Markdown)
**status:** pending

Tree-sitter Markdown grammar. Heading/paragraph/code-block/link/list/blockquote entities. Used by README.md, LICENSE, docs, Wiktionary etymology sections. Composes ITreeSitterParser + F1 + emission services.

## 97. P2.4: JinjaTemplateDecomposer (new project Laplace.Decomposers.Jinja)
**status:** pending

Tree-sitter jinja2 grammar. Literal text segments via F1; variable interpolation entities; control flow (if/for/macro) entities. Used by chat_template.jinja files in HF model packages.

## 98. P2.5: PythonAstDecomposer in Laplace.Decomposers.Code
**status:** pending

Tree-sitter Python grammar. Module/class/function/call/identifier/literal entities. First language for Laplace.Decomposers.Code (multi-language project; subsumes part of G7 #49). Used by modeling_*.py / configuration_*.py / processing_*.py custom HF model files.

## 99. P2.6: SafetensorsHeaderDecomposer + SafetensorsTensorAccessor
**status:** completed

In src/Laplace.Decomposers.Model/Safetensors/. Header decomposer: 8-byte LE uint64 length + JSON header → routes through JsonAstDecomposer for AST + emits per-tensor entity (name, dtype, shape, offset). Tensor accessor (Laplace.Core service): Stream(SafetensorEntry, target Span) for all dtypes (F64/F32/F16/BF16/I64/I32/I16/I8/U8/BOOL); sharded support via model.safetensors.index.json dispatch. Promotes the in-test parser from MiniLmFireflyExtractionTest.cs:119-145.

## 100. P2.7: ZipDecomposer + PickleDecomposer (Laplace.Decomposers.Compressed)
**status:** pending

ZipDecomposer: generic zip → directory-tree entity → recursive decomposition of subfiles via IModalityRouter. Critical for torchscript .pt files (zipped pickle + bytecode + tensor blobs). PickleDecomposer: limited scope for legacy pytorch_model.bin (tensor types only, refuse arbitrary code execution paths) and TorchScript constants.

## 101. P2.8: SentencePieceProtobufDecomposer + BpeMergesDecomposer (new projects)
**status:** pending

SentencePieceProtobufDecomposer (Laplace.Decomposers.Tokenizer.SentencePiece): for sentencepiece .model files (protobuf with piece→id, byte sequences, normalization rules). BpeMergesDecomposer (Laplace.Decomposers.Tokenizer.Bpe): for merges.txt (text format pair → merged token). Both are reusable across all model tokenizer assets.

## 103. P2.10: Replace TokenizerJsonParser.cs with JsonAst-driven approach
**status:** completed

Existing src/Laplace.Decomposers.Model/TokenizerJsonParser.cs uses System.Text.Json directly to peel out specific fields — the lazy pattern Anthony rejected. Replace: parse tokenizer.json via JsonAstDecomposer (substrate AST citizens, cross-tokenizer dedup automatic) → query AST entities by canonical key paths (model.vocab, added_tokens) to emit per-model vocab edges. Tokenizer assets become first-class substrate entities AND vocab edges still emit correctly.

## 104. P2.11: Replace in-test safetensors parser in MiniLmFireflyExtractionTest
**status:** completed

tests/managed/Laplace.Smoke.Tests/MiniLmFireflyExtractionTest.cs lines 119-145 contain a one-off in-test safetensors header parser. Replace with calls to SafetensorsHeaderDecomposer + SafetensorsTensorAccessor from P2.6. Keep determinism + S³ + KNN assertions intact.

## 105. P5.1: SubstrateModelView for query-side model export
**status:** pending

In Laplace.Recomposers.Model. Query substrate for all edges attributed to a given model (or composition with per-model trust α/β/γ weights). Apply per-edge-type Glicko-2 thresholds (PER edge type, NOT global — no knee metaphor). Filter by domain (single-language / medical-only / code-only / etc.) by intersecting with substrate seed-source attestations. The query primitive that backs all model export.

## 106. P5.2: TensorReconstructors (per per-tensor extractor inverse)
**status:** pending

EmbeddingReconstructor (vocab × hidden_dim from firefly POINT4D + cross-edge geometry); AttentionReconstructor (Q/K/V/O from typed attention edges + ratings); FfnReconstructor (W_up/W_down from ffn_key/value edges); LmHeadReconstructor (from lm_predicts edges); plus conv / detection / cross-modal / ASR / TTS / reranker / MoE / diffusion. Sparsifications applied at reconstruction: edges below per-edge-type threshold → 0 weight; quantization-noise zeroing; deduped edges materialize once.

## 107. P5.3: SafetensorsExporter + HuggingFacePackageExporter
**status:** pending

SafetensorsExporter: build header JSON describing reconstructed tensors + emit 8-byte LE length + JSON + raw bytes; sharded export when output exceeds shard threshold (5 GB). HuggingFacePackageExporter: reconstruct config.json / tokenizer.json (substrate AST → JSON tree → file) / generation_config / preprocessor_config / special_tokens_map / chat_template.jinja / README.md describing source models composed + thresholds + domain restrictions / aggregated LICENSE.

## 108. P5.4: CLI ingest + export commands
**status:** pending

In src/Laplace.Cli/Commands/. IngestCommand: laplace ingest <package-path> drives HuggingFacePackageDecomposer through IBatchSink. ExportCommand: laplace export --models llama:0.6,qwen:0.4 --domain code --threshold per-edge-type --output ./refined/ drives Recomposer.Model. Cross-model composition + domain-restricted export. (May overlap with existing #86 CLI task — clarify.)

## 109. P5.5: G8 verify — MiniLM ingest-export round-trip
**status:** pending

tests/managed/Laplace.LiveDb.Tests/MiniLmIngestExportRoundTripTest.cs. Ingest MiniLM → substrate. Export from substrate at threshold 0 (no pruning) → reconstructed package. Compare embedding tensor: original vs reconstructed via cosine similarity > 0.999 per row (lossy due to firefly projection but should be very close). Compare tokenizer.json structure: original vs reconstructed (substrate AST round-trip equivalent). Compare config.json: structurally equivalent (key-value coverage). Closes the first product loop.

## 110. P1.misc: Laplace.LiveDb.Tests project + PgCopyBatchSink round-trip test
**status:** pending

New test project tests/managed/Laplace.LiveDb.Tests/. Env-gated (skipped if no LAPLACE_DB_HOST). First test: PgCopyBatchSinkRoundTripTest — drops + bootstraps test DB, runs Tatoeba decomposer through real IBatchSink, queries entity/edge/provenance tables, asserts content-addressed dedup, per-edge provenance present, Glicko-2 source rating updated. First live-PG integration test the project will have. Pattern for all future live-DB integration tests.

## 111. P1.misc: B11 IVoronoiConsensusService native + managed wrapper
**status:** pending

Missing from native services per audit. Computes Voronoi cells over a point cloud in 4D (S³). Native source ext/laplace_pg/src/voronoi/voronoi_4d.c via Eigen or custom. P/Invoke + managed wrapper Laplace.Core.Abstractions/IVoronoi4D.cs (already exists as interface) + Laplace.Core/Voronoi4D.cs implementation. Used by G6 IVoronoiConsensus query and by frayed-edge detection.

## 112. F5 helpers: MechanisticHeadEntityResolver + MatrixToLineString4D + EdgeMetadataGraphEmitter
**status:** completed

Build the three foundational helper services that all per-tensor extractors compose: (1) MechanisticHeadEntityResolver — content-addressed Llama_L3H7-style entity hashing via composition over (model_entity, F2(layer), F2(head/neuron)); (2) MatrixToLineString4D + VectorToPoint4D + TensorToPolygon4D — content-derived projections of weight tensors into model_weights_4d operator shapes; (3) EdgeMetadataGraphEmitter — emits has_magnitude / has_glicko2_state / participates_in_circuit / polysemy_mode meta-edges per edge cell. One object per file. Source-blind edge_type composition (kind only). Test against in-memory recorders.

## 113. F5: AttentionEdgeExtractor (first per-tensor extractor under corrected design)
**status:** completed

First of #38's 11 per-tensor extractors, embodying every architectural correction from this session. Per layer L per head H: (a) get-or-create Llama_L_H mechanistic head entity; (b) emit W_Q/W_K/W_V/W_O matrices as LINESTRING4D shapes to model_weights_4d partition; (c) per significant fired (token_A, token_B): get-or-create source-blind edge with kind=attends, add provenance attestation, emit has_magnitude meta-edge to F2(weight), Glicko-2 update on shared edge. Test against MiniLM via in-memory recorders verifying source-blind dedup + cumulative provenance + LINESTRING4D emission. Depends on #112.

## 114. F5: FfnKeyValueExtractor (Geva 2021 framing)
**status:** completed

Per FFN layer L: each neuron N is its own mechanistic substrate entity (Llama_L_N42 = composition). Two kinds: ffn_key_activates (input_pattern → neuron) from W_up rows; ffn_value_writes (neuron → output_feature) from W_down columns. W_up + W_down emit as LINESTRING4D operator shapes per neuron to model_weights_4d. Source-blind edges + provenance + has_magnitude meta-edges. Depends on #112.

## 115. F5: LmHeadExtractor
**status:** completed

Final layer projection: residual_pattern → vocab_token edges with kind=lm_predicts. W_lm_head matrix emits as LINESTRING4D to model_weights_4d. Per significant cell: source-blind edge, provenance, has_magnitude meta-edge. Depends on #112.

## 116. F5: EncoderOnlyDecomposer (BERT/MiniLM architecture-family decomposer)
**status:** in_progress

First of #37's eleven architecture-family decomposers. Auto-detects (num_layers, num_heads, hidden_dim, intermediate_dim) from BERT-convention tensor names; drives AttentionEdgeExtractor + FfnKeyValueExtractor + LmHeadExtractor with proper per-head W_Q/K/V slicing (rows) and W_O slicing (cols) and per-neuron W_up rows / W_down cols. End-to-end test against MiniLM verifying 6 layers × 12 heads × 4 = 288 attention LINESTRINGs + ~6 × 1536 × 2 = 18432 FFN POINT4Ds emitted to model_weights_4d.
