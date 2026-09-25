# Assimilation roadmap — corrections recorded 2026-09-25

This note records what the inventor stated and corrected during the 2026-09-25 working session. It also records what the session built and every correction still open. The inventor's words outrank this note, and `docs/INVENTION.md` remains the machine. Where this note conflicts with an older plan document, this note records the newer statement of the inventor. Each workstream below has a GitHub issue under the epic "Assimilation roadmap".

## Finish line

The inventor's statement of it: enough ordinary corpus ingested — WordNet, documents, UD and the rest — for the GPT-like chat the invention describes to work. Model ingestion lets a conventional model be queried directly from SQL through the invention's storage mechanisms. Every workstream below serves one of those two capabilities. They meet in consensus, where the model's knowledge and the corpus's knowledge aggregate.

What sets this apart, in the inventor's words: everyone else asks 10 models, gets 10 answers, and judges them with an 11th. Laplace ingests the 10 as witnesses and asks once. The one asked is the 10 and more, and it gives one answer: their consensus with every other source, each claim carrying its standing and its witnesses.

The inventor's image is the Construct in *The Matrix*: Laplace assimilates the knowledge, and Mold-a-Model exports it as a package for a target, for example the model for a C-3PO. Knowledge packages: #1708, #1425.

---

## 0. Where things stand

The goal is a generative pre-trained transformer rebuilt on the substrate. The forward pass works as follows:

- **Q** is the query plus its context, such as the language.
- **K** is the keys bound to tokens.
- **QK** is query-relative coupling.
- **V** is facts with Glicko-2 standing, read as a distribution. It is never a hard 0/1, and the empty set is an honest answer.
- **O** is realization in the query's language.
- Indexed O(log n) + O(k) lookups and A* paths replace O(n²) attention.
- Every step must be auditable.

The session's work was almost all on the input side: the recipe engine, governed vocabularies, and six sources moved onto recipes. **The forward pass has not been run end to end on a coherent database.** Every seed so far had a defect in its standing inputs: all recipe sources were admitted at trust 1. No read (capital(France), fire/ice, dog vs chien) has been verified since the claim model changed.

---

## 1. Laws stated or clarified in this session

1. **A source is the witness of its observations.** WordNet did not invent "dog". It observed that dog is a noun. The witness identity is the content composition `[authority, release]` (`SourceWitness.Id`). A curated source has one witness per lexicon, for example `[omw-fr, 2.0]`.
2. **Curated sources are mined for knowledge, not recorded bit-perfect.** Records, files and packaging are not content. Only user content needs exact reconstruction.
3. **Normalize to Laplace standards at ingest.** Things that mean the same attest the same, just as the same content has the same hash. Examples: WordNet `n` becomes NOUN, and every language code becomes ISO 639-3. Justify each mapping by a governed authority. Never normalize through synonym attestations, never record raw codes, never fabricate records.
4. **Governed vocabularies are perfcache ROM registries with stable bits.** This covers relations, POS, deprels, languages, entity types, qualifiers and trust classes. They are never seeded as rows.
5. **One relation per meaning.** Variants are multi-select qualifier flags on the attestation mask (`engine/manifest/qualifiers.toml`), which is the sister of the entity highway mask. For example, `eng HAS_EXTERNAL_ID eng {iso639-2b, iso639-2t, iso639-3}` is one claim.
6. **Identifiers are keys, not nodes.** An ILI, an ISO code or a synset key is bound to content by an attestation that also carries the language: `dog —HAS_SENSE→ i46360 @eng`. Keys are the K of the forward pass.
7. **Entities carry OR-masks for filtering.** For example, dog's POS mask is NOUN|VERB. The actual score is a consensus query. A mask miss is never authoritative absence.
8. **Trust is how trustworthy the witness is.** A standards body ranks above an academic curation, which ranks above a user-curated wiki, which ranks above subtitles. Trust enters the standing of every claim the witness makes. The governed trust class is the only statement of trust.
9. **Truths cluster, lies scatter.** High-trust, densely connected witnesses outweigh scattered low-trust ones.
10. **Ownership.** Native C/C++ does the heavy lifting with SIMD/AVX/VNNI, TBB, MKL, Eigen and Spectra. C# and SQL orchestrate. An order of operations that round-trips data through the database is a defect.
11. **Format is generic and semantics are per source.** A standardized format (XML, TSV/CSV, JSON/JSONL, RDF Turtle, CoNLL-U, PGN, safetensors, Parquet) has one decomposer. That decomposer ingests any file of the format as ordinary digital content. A curated source's recipe names its format, receives that decomposer by injection, and adds only the semantic layer: claims, vocabularies, qualifiers and witness.
12. **A model is ingested as records, not blobs, and never by prompting.** A checkpoint's parameters are ETL'd into Laplace records, so the Laplace forward pass (SQL-orchestrated, native-executed) can run the model's computation. It runs through indexed, filtered lookups instead of brute force, and every value is addressable by model, tensor, layer, head, row and column. The model's learned relations, such as king–queen, become graded attestations under the model witness. They then aggregate with every other source, and that is how Laplace assimilates knowledge and capability.
13. **Record only what is above the model's floor.** Laplace does not record everything (the lottery ticket hypothesis). The floor is detected from the model's own statistics, not set by a constant noise floor or a top-k.
14. **Shape narrows what a tensor is; the name is a convention, not a specification.** Operator roles (attention, convolution, MLP, diffusion blocks, embeddings) are recognized structurally. Names, config and weight statistics are further evidence.
15. **Software is reversible: what can be ingested can be exported (Mold-a-Model, `docs/specs/12_Mold_A_Model_Synthesis_Map.txt`).** Model records must carry enough to construct a target model back out through the declared export mapping (Foundry). The pooled construction is one consensus program over every model witness and every other source. It is not a merge of compatible tensors or N answers judged by an N+1th model (spec 12, round-table law).
16. **Look at the forest.** Every defect is a system-wide pattern to fix across the substrate, decomposers and read path. Code comments or issue claims that conflict with the invention's logic are drift.

---

## 2. Built in this session

| Commit | What |
|---|---|
| `c915ecca8` | A source is the witness: `SourceWitness.Id(authority, release)`, per-lexicon witnesses via recipe `witnessFields`, SQL `laplace.witness_id` |
| `98341697e` | Governed qualifier law (`qualifiers.toml`, codegen). Static and sibling-field qualifiers, OR-merged under aggregation. UCD case mappings, foldings, decompositions and names as one relation each plus qualifiers. Delimited `headerLines` |
| `161507ab5` | ISO 639-3 source generation (`recipes/iso639/20260415`) |
| `c66ec31e3` | CILI source generation (`recipes/cili/a895d7e`). A hand-written Turtle reader (`recipe_turtle.hpp`, to be replaced, see D). Score and observation fields lower after the claims they grade |
| earlier | WN-LMF claims model (OEWN, OMW), POS/language/deprel laws, UD parse lowering, forward-pass SQL primitives, direct word→key sense readers |

**Uncommitted at the time of writing, committed with this note:**
- A generation's prior now comes from its trust class via `SourceTrust.ForClass`, replacing the numeric `"trust": 1`.
- Unicode and ISO 639-3 are StandardsDerived; OEWN, OMW, CILI and UD are AcademicCurated.
- The fake `HAS_TRUST_CLASS` testimony is removed. Calculated evidence carries the `derivation/calculation` qualifier on each claim. `query_evidence.c` reads that qualifier from the observation rows instead of looking up the source's trust class.
- The attestation mask is qualifiers only; C# no longer stamps the relation's highway bit into it.

---

## 3. Workstreams

### A. Standing math

- **Every recipe seed ran at trust 1.** This is fixed by trust from class (above). A full reseed is required.
- **`witness_weight = rank × trust` drives both the opponent rating and the RD** (`engine/core/src/attestation_engine.c`, `laplace_attestation_witness_phi` / `_opponent_rating`). Certainty and salience are therefore one number. For example, a Unicode `HAS_SCRIPT` fact (0.95 × 0.08) plays as a weak *and uncertain* witness (rating 1229, RD 326), when it is certain and merely low-salience.
- **Relation rank is a read-time salience weight** (`relation_types.toml` `[ranks]`, "recalibrated for semantic salience (recall)"). It is baked into write-time standing.
- **Target:** trust sets certainty (RD). Relation rank either stays out of the fold and weights reads (QK coupling), or scales the pull without touching certainty. *Decision for the inventor.*
- **Trust classes are `blake3("substrate/trust_class/X/v1")` strings resolved by an if-chain** (`SourceTrust.ForClass`). Chess uses classes the chain does not know (`UserPromptContent`, `ResponseContent`). **Target:** a governed trust-class registry (manifest + codegen), like the other laws.
- Related: #1303, #1321, #1015.

### B. Consensus keeps the query's context

- **Consensus cells are `(subject, relation, object)` with context folded away.** Language, sense and qualifiers therefore collapse at fold. English "chat" and French "chat" merge, and "which ISO code" disappears. V cannot be conditioned by Q from consensus.
- **Target:** the cell identity or a companion structure preserves the context dimension the forward pass conditions on. Qualifier masks OR-fold into the cell.
- Related: #1052, #1401.

### C. ETL ownership and order of operations

Measured and read in code:

- **Evidence round-trips through PostgreSQL.** The native stage builds claims in memory. C# copies them into `attestations`. The fold then reads every touched cell's durable evidence back over SPI and refolds all replayable testimony from neutral, on every working set that touches the cell (`extension/laplace_substrate/src/fold_route.c` header, `consensus.evidence_rows_typed`).
- **Deduplication by database existence probes.** The OMW seed made 434 round trips at about 8.4k rows/s. Local materialize of UCD produces about 300k rows/s.
- **The highway mask** is deposited in a separate SQL pass per chunk (`consensus.highway_mask_deposit`).
- **Legacy decomposers do per-row C# work** (`SubstrateChangeBuilder` dictionary merge, `AttestationMergeMath`).

**Target order:**

1. **Native, per source:** parse, compose ids, emit claims. Then reduce by attestation id (merge games and scores, OR masks). Then fold the witness's rating period per cell in memory. Then route to partitions and emit sorted COPY streams, consensus deltas and entity masks.
2. **PostgreSQL:** stores the rows and resolves key conflicts. It reads prior standing once per cell per source.
3. **C#:** selects artifacts, orders dependencies, and handles commit epochs, progress and retries. It holds no per-row objects.
4. **SQL:** set-level orchestration only.

**Correcting a source becomes per-witness replacement,** not a database recreate: evict the witness's claims, refold the touched cells, re-ingest. `seed-foundation.yml` already has `replace_existing` + `confirm_replace`. **Seeds apply the locally materialized rows** rather than re-decomposing.

Related: #1714, #1292, #964, #952, #1409, #967.

### D. Format decomposers + recipes (dependency injection)

**Two parallel stacks do the same job:**
1. The grammar stack (`GrammarDecomposer`, `StructuredGrammarIngest`, `GrammarRowComposer`, `IGrammarWitness`). Its vendored tree-sitter grammars already include JSON, CSV, TSV, **Turtle**, XML and Markdown (`engine/core/grammars/CMakeLists.txt`), plus a homegrown PGN grammar. Its semantics are hand-written C# per source (`WiktionaryGrammarWitness`, `SemLinkGrammarWitness`).
2. The recipe engine (`engine/core/src/recipe_stream.cpp`). It has its own XML, delimited and Turtle readers and declarative semantics. `recipe_turtle.hpp` is a hand-written reader for a format the engine already parses.

**Target:**
- One format-decomposer interface. Each implementation turns bytes into structure (nodes or records with fields and positions) and exposes that structure as the file's content physicality. It is registered by format id and injected. It is a streaming reader where files are huge (Wiktextract, FrameBase, UCD XML).
- A generic content decomposer over any format.
- The recipe as a semantic decorator per curated source.
- Remove `recipe_turtle.hpp`, the readers built into `recipe_stream.cpp`, and the per-source grammar witnesses.

**Formats in the estate:**

| Format (standard) | Datasets |
|---|---|
| XML (W3C) | WN-LMF (OEWN, OMW 2.0), UCD XML (UAX #42), FrameNet, VerbNet, PropBank frames, UD metadata, LoC ISO 639, ISO 639-5 Atom/RSS |
| RDF: Turtle, RDF/XML (W3C) | CILI `.ttl`, FrameBase 2.0 `.ttl.gz`, ISO 639 `.rdf` |
| TSV / CSV (RFC 4180) | ISO 639-3 tables, CILI maps, OMW `.tab`, Tatoeba, ConceptNet (TSV + JSON column), ATOMIC 2020, Lichess openings, GeoNames, VerbNet/PropBank/SemLink indexes, UCD `.txt` (UAX #44) |
| JSON / JSON Lines (RFC 8259) | Wiktextract (2.8 GB gz), ATOMIC `.jsonl`, SemLink JSON, Safety sets |
| CoNLL-U | UD 2.18 |
| PGN, FEN/EPD | TWIC, chess corpora |
| Parquet (Apache) | Safety sets |
| WNDB (`wndb(5)`) | WordNet 3.0 |
| Moses parallel text | OpenSubtitles |
| safetensors | `/vault/models` (see E) |
| BibTeX, TOML, Markdown, HTML | OMW citations and index, documentation, SIL pages |

Related: #1045, #1177, #1153, #1403, #1713.

### E. Model ingestion — TinyLlama first

**Target checkpoint:** `/vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0`. It is `LlamaForCausalLM` in bf16: 22 layers, d = 2048, 32 query heads, 4 KV heads, head_dim 64, SwiGLU 5632, vocab 32000, untied `lm_head`, RoPE θ 10000, RMSNorm. It has 201 tensors.

**E1. The safetensors format decomposer.** The header is an 8-byte length, a JSON map (tensor name → dtype, shape, byte offsets) and a raw buffer. The decomposer is native and memory-mapped, with native dtype decoding. It does not use GGUF/AWQ, which are lossy.

**E2. Operator recognition by shape.** The name is a hint; the shape is the constraint. Operator templates are declared in a governed manifest: slots with shape constraints over the symbols *d, V, h, h_kv, d_h, f, E, r, C, k*. Recognition works as follows:
1. Find *d* (the dimension present in every block), V (equal to the tokenizer size) and the block repetition from dimension statistics.
2. Solve the template constraints.
3. Take the symbols the shapes leave open from config.
4. Break slot symmetries (Q vs O, K vs V, gate vs up) with name hints and weight statistics.
5. Keep unresolved assignments ambiguous, with scores on each option.

Templates cover the following (TinyLlama's dimension frequency is 2048×245, 5632×66, 256×44, 32000×2):
- embedding and unembedding `[V, d]`
- norm gains `[d]`
- self-attention with GQA, including fused QKV in either orientation
- cross-attention, where K/V read a width different from Q
- gated and plain MLPs
- MoE: E MLP groups plus a router `[E, d]`
- low-rank factors: MLA, LoRA
- convolutions (`[out, in, kh, kw]`, depthwise, pointwise)
- diffusion UNet blocks
- DiT and ViT patch embeddings and adaLN modulation
- audio conv1d front ends
- learned positions

This replaces `ArchitectureProfile` (four hardcoded families; anything else throws mid-run) and `TensorRoleClassifier`.

**E3. Tokenizer.** The vocabulary and merges become content on the text ladder. Each token id is a key bound to that content under the model witness, and the id is kept (it is lost today).

**E4. The floor, from the model's own statistics.** There is no constant and no top-k.
- **Per matrix and per head slice:** compare the singular-value spectrum against the Marchenko–Pastur bulk (Martin & Mahoney; WeightWatcher). The signal rank comes from the Gavish–Donoho optimal hard threshold `ω(β) · median(σ)`. The computation is a native MKL SVD.
- **Per token pair in a circuit:** compute a significance against the matrix's own bulk noise. Record a claim only if its game would move standing by more than its own uncertainty. A chance-level claim plays as a draw at maximum RD and carries no information.
- **Per model:** the recorded subnetwork is measured against the dense model on held-out text (perplexity). This is the lottery-ticket check.
  - One caveat: LLM spectra are heavy-tailed, so a low-rank cut alone can cost more than sparse pruning (SparseGPT and Wanda reach about 50% one-shot).
  - A layer whose cut costs perplexity had its floor set too high. Its power-law exponent says why.

**E5. Records.**
- Each matrix's signal components (token and feature loadings) are recorded as trajectories under the model witness, plus the sparse residual weights that stay significant.
- A value's address is `[model witness, tensor, row, col]`. The FACTOR vertex class already carries 6 float32 per vertex, and bf16 would carry 12.
- Circuit claims:
  - QK `W_Eᵀ W_Qᵀ W_K W_E`, OV `W_U W_O W_V W_E` and the direct path `W_U W_E` (Elhage et al.)
  - MLP key and value neurons (Geva et al.)
  - projections of any parameter into vocabulary space (Dar et al.)
  - expert routing
- These are attestations between tokens under the model witness, with the circuit (tensor role, layer, head, neuron or expert) as context and the circuit kind as a qualifier. They are written whether or not another source already holds the pair.

**E6. The native forward pass over the records.**
- **Kernels:** AVX-512 BF16/VNNI dot products, TBB over heads, MKL.
- **Filtered, not brute force:** only the heads and neurons a token engages (Deja Vu: more than 80% of heads and 95% of MLP parameters are inactive per token), and top candidates at the output layer through an index rather than all 32,000 rows.
- **Acceptance:** Laplace's TinyLlama matches the dense model within the model's own noise on held-out text.

**E7. The round trip through Mold-a-Model.** Export TinyLlama back out of its Laplace records through the spec 12 mapping (Foundry) as safetensors. Run the export in a conventional runtime and compare it with the original on held-out text. Then export a pooled model built from several model witnesses plus the curated corpus. This proves the ingest kept what matters and that export is the inverse.

**Existing model code** (`app/Laplace.Decomposers/Model`, about 5,300 lines, audited 2026-09-25): the maths is native (MKL), no model is prompted, and every relation is governed. Its defects:
- **Weights are discarded after scoring**, so no forward pass is possible. `MODEL_INGESTION_DESIGN.md` §1 "numeric values … transient operands" is superseded by E5.
- **Circuit identity omits the model** (`ModelCoordinates.cs`: `model-circuit/<plane>/layer/N/head/M`), so two models' L3H5 collide.
- **Pair claims only re-score pairs already in consensus** (`ModelTokenEdgeETL.cs:251-325`), so a fresh database gets no model knowledge.
- **Each circuit's trajectory is the entire vocabulary**, ranked by one token's norm.
- **REFUTE comes from the sign of a raw dot product.**
- **The FFN stage is a full nonlinear per-token probe in double precision.**
- **Norms, RoPE, MoE, MLA and LoRA are unhandled.**
- **Bookkeeping ids:** `Blake3` recipe and tokenizer entities, `OfCanonical` special tokens and contexts.
- **Tokens lose their ids** and collapse when they normalize alike.

Related: #1015, #1074, #1344, #1362, #1111, #1054, #1034. Export: `docs/specs/12_Mold_A_Model_Synthesis_Map.txt`, `docs/specs/09_Substrate_LM_Synthesis.txt`.

### F. The native forward pass over curated knowledge (read path)

- **`functions/converse/forward_pass.sql.in`** (`converse.bindings`, `key_facts`, `couple`, `terms_language`) does its heavy lifting in SQL. It moves to native code with SQL orchestrating.
- **Still hand-rolled:** about 27 word→sense readers, 61 id renderers, 130 consensus-by-subject readers and 124 C# inline reads.
- **Realization (O) is English/Bulgarian templates** (`chat_scaffold`). `prompt_coherence.c` matches prompt words against English relation labels.
- **Attestation endpoints do not name a tier.** The tier says which physicality of an entity is meant.
- **Acceptance:** on one coherent seed, these reads return standing distributions, realized in the query's language, with an auditable path:
  - capital(France) → Paris
  - fire/ice and elephant/electricity contrasts
  - dog vs chien
- Related: #1401, #1478, #1018, #1099, #1016, #1178, #1047.

### G. Fake machinery still to remove

- `SubstrateCanonicalIds.Source(name)` `blake3` source ids for every legacy decomposer and every chess source
- chess marker entities (`AnalysisMarkerId`)
- completion markers (`HasLayerCompleted` / `HasUnitCompleted`)
- the `canonical_names` table, `register_canonical(s)`, and `CanonicalNamesForReadback` in 39 decomposers
- `laplace.source_id`
- the `'language:eng'` and `operation/what_is/v1` SQL literals
- `ByteAtoms` as a second tier-0 alphabet
- `converse.session_topics`
- the trust-class `blake3` keys (see A)
- the `CalculationSources` static registry added in this session: a hidden global populated by a module initializer, to be replaced by a governed declaration

Related: #1038, #1049, #1052.

### H. Sources onto recipes

- **Done:** Unicode, ISO 639-3, CILI, OEWN, OMW.
- **UD** has a recipe but has never been seeded.
- **Still legacy:** WordNet 3.0 (to retire in favour of omw-en + CILI), Wiktionary, ConceptNet, VerbNet, PropBank, FrameNet, SemLink, VerbAtlas, FrameBase, ATOMIC, Tatoeba, OpenSubtitles.
- **Dataset priority by dependency:** Unicode, ISO 639, CILI, then OEWN/OMW, Wiktionary, frames/roles, commonsense, then usage corpora. UD, Wiktionary and ConceptNet also test the two ingest shapes: many unbalanced files versus huge single files.
- **Trust classes:** Wiktionary is UserCuratedResource, and OpenSubtitles is lower.
- **Open decisions:**
  - Wiktionary senses without a Wikidata id have no key; the legacy decomposer mints `OfCanonical` sense entities. *Decision for the inventor.*
  - CILI `sense-mappings/*.tab` (sense key → offset per PWN release) are marked unsupported until the sense-key consumers migrate.
  - ISO 639-2 French names, CLDR and ISO 639-5 each need their own witness.

Related: #1713, #1471, #1057, #1180.

### I. Vocabulary governance

- **The relation manifest still carries the variants the qualifier law replaces:** `HAS_ISO639_*_CODE`, `HAS_UPPERCASE_MAPPING` and siblings, `HAS_NAME_ALIAS`, and others. Legacy decomposers and gates still use them.
- **The highway has about 30 bits left.**
- **Entity OR-masks (the POS mask and its sisters) are not built.**
- **Qualifiers never reach consensus** (see B).
- Related: #1133, #1712.

### J. Process

- **Before a push:** run `cookbook materialize` for every changed recipe (it runs the same native code the seed runs), and replay changed SQL in manifest order inside `BEGIN … ROLLBACK` on the live database. Five pipeline failures in this session would have been caught locally: a reserved column name, nested window functions, undeclared entity types, a missing gate entry, and an undeclared qualifier.
- **Before reporting a result:** look at the substrate (entity census by type and tier, `/explore`), not only claim counts.
- **Tests:** much of the suite asserts removed behaviour. Tests touched in this session were only kept compiling.
- Related: #1315, #1374, #1674.

---

## 4. Decisions for the inventor

1. **Relation rank:** out of the fold (read-time weight only), or scaling pull without certainty. See A.
2. **Consensus context:** which context dimension a cell keeps. See B.
3. **Wiktionary sense keys** when no Wikidata id exists. See H.
