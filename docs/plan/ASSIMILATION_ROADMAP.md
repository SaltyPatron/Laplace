# Assimilation roadmap — corrections recorded 2026-09-25

This note records what the inventor stated and corrected during the 2026-09-25 working session. It also records what the session built and every correction still open. The inventor's words outrank this note, and `docs/INVENTION.md` remains the machine. Where this note conflicts with an older plan document, this note records the newer statement of the inventor. Each workstream below has a GitHub issue under epic #1725.

## Finish line

**What the invention is (inventor):** a reinvention of the generative pre-trained transformer that completely eliminates the GPU requirement, while staying compatible with conventional AI and able to consume it.
- **The reinvention.** GPT's functions are performed through Laplace's own mechanism: GPT's steps mapped to semantic instruction sets (spec 36 forward program, spec 37 ISA), witnessed structure with Glicko-2 standing instead of learned weights, typed indexed response instead of dense attention, executed as SQL over native code.
- **No GPU.** The semantic engine runs on CPU through native code and PostgreSQL by design (spec 36 acceptance; Laplace-Refactor constitution).
- **Compatibility and consumption.** Conventional checkpoints are consumed as witnesses whose circuits become graded evidence. The OpenAI-compatible endpoint and MCP serve the same machine to existing tools (INVENTIONS #79). Mold-a-Model exports conventional model artifacts from current standing (spec 12).

The inventor's statement of it: enough ordinary corpus ingested — WordNet, documents, UD and the rest — for the GPT-like chat the invention describes to work. Model ingestion lets a conventional model be queried directly from SQL through the invention's storage mechanisms. Every workstream below serves one of those two capabilities. They meet in consensus, where the model's knowledge and the corpus's knowledge aggregate.

What sets this apart, in the inventor's words: everyone else asks 10 models, gets 10 answers, and judges them with an 11th. Laplace ingests the 10 as witnesses and asks once. The one asked is the 10 and more, and it gives one answer: their consensus with every other source, each claim carrying its standing and its witnesses.

The inventor's image is the Construct in *The Matrix*: Laplace assimilates the knowledge, and Mold-a-Model exports it as a package for a target, for example the model for a C-3PO. Knowledge packages: #1708, #1425.

---

## 0. Where things stand

Laplace is a complete reinvention of the transformer as a SQL-executed architecture over a universal persistent substrate (Laplace-Refactor `docs/product/CONSTITUTION.md` §1, written from the inventor's direct requirements). It is not a faster transformer and does not replay transformer math. It obtains what conventional models buy with dense parameter computation from explicit reusable structure, sparse indexed response, uncertainty-bearing evidence and native execution (`INVENTION.md` §8 and §20).

The Laplace forward pass is the program `RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS` (spec 36, spec 37). The transformer roles correspond only functionally (`INVENTION.md` §8, INVENTIONS #71):

- **Q** is the active admitted observation, its discourse bindings and its open obligations.
- **K** is the indexed typed addresses and planes able to respond.
- **QK** is COUPLE: the typed, query-relative response field. It is not one relevance score.
- **V** is the responding physicalities, facts, evidence and calculations, with standing as rating, deviation, volatility and witness count. It is not one opaque confidence or a normalized distribution.
- **O** is the receipted fold into updated bindings, orientation, frontier and obligations. Rendering a surface is REALIZE, a separate stage.
- A*, walks and geometry are operators inside the program, not the cognition itself (INVENTIONS #69).

The session's work was almost all on the input side: the recipe engine, governed vocabularies, and six sources moved onto recipes. **The forward pass has not been run end to end on a coherent database.** Every seed so far had a defect in its standing inputs: all recipe sources were admitted at trust 1. No read (capital(France), fire/ice, dog vs chien) has been verified since the claim model changed.

---

## 1. Laws stated or clarified in this session

1. **A source is the witness of its observations.** WordNet did not invent "dog". It observed that dog is a noun. The witness identity is the content composition `[authority, release]` (`SourceWitness.Id`). A curated source has one witness per lexicon, for example `[omw-fr, 2.0]`.
2. **Curated sources are mined for knowledge, not recorded bit-perfect.** Records, files and packaging are not content. Only user content needs exact reconstruction.
3. **Normalize to Laplace standards at ingest.** Things that mean the same attest the same, just as the same content has the same hash. Examples: WordNet `n` becomes NOUN, and every language code becomes ISO 639-3. Justify each mapping by a governed authority. Never normalize through synonym attestations, never record raw codes, never fabricate records.
4. **Governed vocabularies are perfcache ROM registries with stable bits.** This covers relations, POS, deprels, languages, entity types, qualifiers and trust classes. They are never seeded as rows.
5. **One relation per meaning.** Variants are multi-select qualifier flags on the attestation mask (`engine/manifest/qualifiers.toml`), which is the sister of the entity highway mask. For example, `eng HAS_EXTERNAL_ID eng {iso639-2b, iso639-2t, iso639-3}` is one claim.
6. **An external identifier is not a node to render (inventor).** An ILI is not dog's label; it is an identifier bound to content by a witnessed claim. In the ingest law an opaque external identity is a typed reference (`INGEST_BOUNDARY_AND_RECIPE_LAW.md`). *Correction:* earlier versions of this note called identifiers "the K of the forward pass". That was the session's paraphrase, not the invention; K is every indexed typed address and plane able to respond.
7. **Entities carry OR-masks for filtering.** For example, dog's POS mask is NOUN|VERB. The actual score is a consensus query. A mask miss is never authoritative absence.
8. **Trust is how trustworthy the witness is.** A standards body ranks above an academic curation, which ranks above a user-curated wiki, which ranks above subtitles. Trust enters the standing of every claim the witness makes. The governed trust class is the only statement of trust.
9. **Truths cluster, lies scatter.** High-trust, densely connected witnesses outweigh scattered low-trust ones.
10. **Ownership.** Native C/C++ does the heavy lifting with SIMD/AVX/VNNI, TBB, MKL, Eigen and Spectra. C# and SQL orchestrate. An order of operations that round-trips data through the database is a defect.
11. **Format is generic and semantics are per source.** A standardized format (XML, TSV/CSV, JSON/JSONL, RDF Turtle, CoNLL-U, PGN, safetensors, Parquet) has one decomposer. That decomposer ingests any file of the format as ordinary digital content. A curated source's recipe names its format, receives that decomposer by injection, and adds only the semantic layer: claims, vocabularies, qualifiers and witness.
12. **A model is ingested as Laplace records, never as blobs and never by prompting.** Tensor values are transient calculation operands. The durable product is canonical structure, source-scoped circuit physicalities and trajectories, and graded evidence: the model's learned relations, such as king–queen, as attestations under the model witness (`INVENTION.md` §8 and §15, INVENTIONS #53 and #54, spec 09 storage law). Layer, head and expert are source-scoped structural coordinates of that evidence (INVENTIONS #58). The model's evidence aggregates with every other source's and is read through the Laplace forward program. Laplace does not replay the model's computation over retained weights (INVENTIONS, product identity: "not a GPU replay of retained weights").
13. **Record only what carries information (inventor: the lottery ticket).** Store irreducible admitted information once, and do not persist world-all-pairs (INVENTIONS #102; model design §4). Which derived evidence is significant is decided by a declared calculation contract over the model's own statistics (INVENTIONS #106), not by a constant floor or a top-k.
14. **Shape narrows what a tensor is; the name is a convention, not a specification.** Operator roles (attention, convolution, MLP, diffusion blocks, embeddings) are recognized structurally. Names, config and weight statistics are further evidence.
15. **What is ingested can be exported (Mold-a-Model, spec 12).** Export is a recipe over current standing, geometry and selected operators: a filtered snapshot ("I know kung fu"), not a copy of an ingested checkpoint and not bit-perfect reconstruction (`INVENTION.md` §15; AGENTS.md). A pooled construction is one consensus program over every witness. It is not a tensor merge and not N answers judged by an N+1th (spec 12, round-table law). An export is validated by loading in an external runtime and passing held-out semantic and source-ablation tests (spec 09), not by comparing it with the ingested checkpoint.
16. **A leaf spring is not a watch (inventor).** Remove any component of a watch and it stops working, and Laplace is no different. Identity, physicality and trajectory, witnessed bindings, governed vocabularies, typed standing, the forward program, realization, native execution and export work only together. A component's own measure (claim counts, a passing primitive, a green seed) is not progress on the machine. Substituting a conventional part for a component breaks the whole. Work is accepted as vertical slices through every component, read live.
17. **Personality firmware operates the tool; it is not the knowledge (inventor).** Laplace's world is a tool, like knowing that guns exist. Knowing that does not decide what anyone does with it. The inventor maps the individual steps of a conventional GPT to semantic instruction sets: the operation ISA (spec 37) and the forward program (spec 36), with the transformer-slot map as the correspondence. Personality firmware is the program over those instructions that decides observation, orientation, decision, action and generation. The Gödel engine is its OODA loop, and its own outputs are witnessed back as inputs (archived spec 15: "evaluation IS ingestion"). The same world under different firmware behaves differently. Firmware never edits the knowledge, and the knowledge never dictates the firmware. This matches knowledge/authority/compute separation and governance without epistemic erasure (CAPABILITIES.md; INVENTION.md §16).
18. **Learning without gradient descent (inventor).** Stochastic gradient descent is not reinvented; it is removed, because ingestion is the learning process (`INVENTION.md` §11; the slot map gives training loss as witnessed outcomes and the uncertainty-bearing fold). Reinforcement learning is reinvented: an action's observed consequence is witnessed and folds into Glicko-2 standing (OODA's observe-consequence; "evaluation is ingestion"). Firmware policies are rated by their outcomes, so what is reinforced is attributable standing, not an opaque weight change.
19. **Gaps are knowledge too: a periodic table of knowledge (inventor, parked).** The structure of what is known exposes what is missing, as Mendeleev's table predicted undiscovered elements. Frayed edges and gaps feed the Gödel incompleteness lane (#1726, #1048). This is also where sense priors belong (#371), not in the engine attesting its own disambiguations.
20. **Export comes after the reads are right (inventor).** Mold-a-Model generates every slot (embeddings, norms, gate/up/down, heads, layers) by querying standing. Until the forward program reads correctly, export work muddies the data, so export issues are P3.
21. **Look at the forest.** Every defect is a system-wide pattern to fix across the substrate, decomposers and read path. Code comments or issue claims that conflict with the invention's logic are drift.

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

Issue: #1715

- **Every recipe seed ran at trust 1.** This is fixed by trust from class (above). A full reseed is required.
- **`witness_weight = rank × trust` drives both the opponent rating and the RD** (`engine/core/src/attestation_engine.c`, `laplace_attestation_witness_phi` / `_opponent_rating`). Certainty and salience are therefore one number. For example, a Unicode `HAS_SCRIPT` fact (0.95 × 0.08) plays as a weak *and uncertain* witness (rating 1229, RD 326), when it is certain and merely low-salience.
- **Relation rank is a read-time salience weight** (`relation_types.toml` `[ranks]`, "recalibrated for semantic salience (recall)"). It is baked into write-time standing.
- **Decided (inventor, 2026-09-25):** a claim is "this source says X is (or is not) Y": the witness, the outcome and the games. Its standing comes from the witness's trust. Relation rank (synonymy vs homonymy vs stop-word glue) is salience and applies at reading, in QK coupling. "Hot is not cold" is a refutation of hot IS cold, and "hot is antonymous with cold" is a confirmation of antonymy; neither gains or loses certainty from its relation's salience. Done: every native builder and the recipe stream use the witness's trust alone.
- **Trust classes are `blake3("substrate/trust_class/X/v1")` strings resolved by an if-chain** (`SourceTrust.ForClass`). Chess uses classes the chain does not know (`UserPromptContent`, `ResponseContent`). **Target:** a governed trust-class registry (manifest + codegen), like the other laws.
- Related: #1303, #1321, #1015.

### B. ~~Consensus keeps the query's context~~ (withdrawn)

Issue: #1716 (closed)

This was mis-framed. A cell does not need a context dimension, because the key model already keeps context where it belongs:
- A word binds to a language-neutral key (`dog —HAS_SENSE→ i46360 @eng`, `chien —HAS_SENSE→ i46360 @fra`). The binding carries the language, and the witness is the lexicon itself (`[omw-fr, 2.0]`).
- Facts about a key (`i46360 IS_A …`) are language-neutral, so one consensus over every witness is correct.
- A cross-language homograph binds to different keys. English "gift" and German "Gift" (poison) are different cells because the objects differ.
- A shared claim such as "chat HAS_POS NOUN" is true in both languages, so aggregating it is correct.
- The forward pass reads Q's language through the bindings, which are attestations indexed by subject and context. It reads V from consensus on the key, and O realizes the key through the bindings in the query's language.
- A claim's qualifiers are read from its attestations.

### C. ETL ownership and order of operations

Issue: #1717

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

Issue: #1718

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

Issue: #1719

**Target checkpoint:** `/vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0`. It is `LlamaForCausalLM` in bf16: 22 layers, d = 2048, 32 query heads, 4 KV heads, head_dim 64, SwiGLU 5632, vocab 32000, untied `lm_head`, RoPE θ 10000, RMSNorm. It has 201 tensors.

The contract is `MODEL_INGESTION_DESIGN.md`, `INVENTION.md` §8, §14 and §15, INVENTIONS #53–#58, and spec 09.

- **E1. safetensors provider.** A native, memory-mapped container reader whose values are decoded in bulk as transient operands (see D).
- **E2. Source-role recognition by shape.** The name is a hint (INVENTIONS #99: tensor names are contextual realizations). Shape constrains the role. Roles are source-scoped coordinates, not native ontology (INVENTIONS #58). This replaces `ArchitectureProfile` (four hardcoded families) and `TensorRoleClassifier`. TinyLlama's dimension frequency is 2048×245, 5632×66, 256×44 and 32000×2.
- **E3. Tokenizer.** Decodable pieces resolve to shared canonical content. Model-local pieces and ids remain source-local references and occurrences. BPE fragments never become words (design §3).
- **E4. Significance as a declared calculation.** Which circuit evidence carries information is a calculation contract over the model's own statistics (INVENTIONS #106), for example a spectral bulk-versus-signal test. It is not a constant floor or a top-k. No world-all-pairs is persisted (INVENTIONS #102).
- **E5. Durable records.**
  - Source-scoped circuit entities and physicality trajectories, with the model in their identity.
  - Graded evidence between canonical entities, under the model witness, with refutation never inferred from a dot-product sign (design §6).
  - No raw values in any form (#1344).
- **E6. Reading.** The model's evidence participates in the Laplace forward program like any other witness. Source-scoped A, B and pooled A+B are inspection scopes.
- **E7. Export through Mold-a-Model** (spec 12), validated as spec 09 requires.

**Existing model code** (`app/Laplace.Decomposers/Model`, audited 2026-09-25): the maths is native, no model is prompted, weights are not retained, and every relation is governed. Its defects:
- **Circuit identity omits the model**, so two models' L3H5 collide.
- **Pair claims only re-score pairs already in consensus**, so a fresh database gets no model knowledge.
- **REFUTE comes from the sign of a raw dot product.**
- **The FFN stage is a full nonlinear per-token probe in double precision.**
- **Norms, RoPE, MoE, MLA and LoRA are unhandled.**
- **Bookkeeping ids:** `Blake3` recipe and tokenizer entities, `OfCanonical` special tokens.
- **Model-local token ids and occurrences are lost.**

Related: #1015, #1074, #1344, #1362, #1111, #1054, #1034.

### F. The native forward pass over curated knowledge (read path)

Issue: #1720

- **`functions/converse/forward_pass.sql.in`** (`converse.bindings`, `key_facts`, `couple`, `terms_language`) does its heavy lifting in SQL. It moves to native code with SQL orchestrating.
- **Still hand-rolled:** about 27 word→sense readers, 61 id renderers, 130 consensus-by-subject readers and 124 C# inline reads.
- **Realization (O) is English/Bulgarian templates** (`chat_scaffold`). `prompt_coherence.c` matches prompt words against English relation labels.
- **Attestation endpoints do not name a tier.** The tier says which physicality of an entity is meant.
- **The session's own primitives flatten typed state.** `converse.couple` reduces coupling to one weight (a `1/ln(2 + count)` specificity). `converse.key_facts` normalizes standing into a share, which behaves like a softmax. Both violate spec 36 COUPLE and `INVENTION.md` §19 ("typed state stays typed"). They are rebuilt as typed responses.
- **Acceptance:** on one coherent seed, these reads return typed standing (rating, deviation, volatility, witnesses), realized in the query's language, with an auditable path:
  - capital(France) → Paris
  - fire/ice and elephant/electricity contrasts
  - dog vs chien
- Related: #1401, #1478, #1018, #1099, #1016, #1178, #1047.

### G. Fake machinery still to remove

Issue: #1721

- `SubstrateCanonicalIds.Source(name)` `blake3` source ids for every legacy decomposer and every chess source
- chess marker entities (`AnalysisMarkerId`)
- completion markers — done: ingest completion is operational state (`ingest_unit_completion`, `ingest_layer_completion`), never attestations
- the `canonical_names` table, `register_canonical(s)`, and `CanonicalNamesForReadback` in 39 decomposers
- `laplace.source_id`
- the `'language:eng'` and `operation/what_is/v1` SQL literals
- `ByteAtoms` as a second tier-0 alphabet
- `converse.session_topics`
- the trust-class `blake3` keys (see A)
- the `CalculationSources` static registry added in this session: a hidden global populated by a module initializer, to be replaced by a governed declaration

Related: #1038, #1049, #1052.

### H. Sources onto recipes

Issue: #1722

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

Issue: #1723

- **The relation manifest still carries the variants the qualifier law replaces:** `HAS_ISO639_*_CODE`, `HAS_UPPERCASE_MAPPING` and siblings, `HAS_NAME_ALIAS`, and others. Legacy decomposers and gates still use them.
- **The highway has about 30 bits left.**
- **Entity OR-masks (the POS mask and its sisters) are not built.**
- **Qualifiers never reach consensus** (see B).
- Related: #1133, #1712.

### K. Personality firmware and the Gödel engine

Issue: #1726

- **Documented today:**
  - the OODA loop and self-witnessing (archived spec 15);
  - the ISA-as-processor framing (#823);
  - the opcodes (spec 37) and the forward program (spec 36);
  - "governance/firmware" as one term of the effective mind (CAPABILITIES.md);
  - game firmware as rules over the same world (`docs/guides/knowledge-arena.md`).
- **Not documented:**
  - personality firmware as a named, content-addressed program over the ISA;
  - which forward-program stages it parameterizes (ORIENT goals, ROUTE, STEER and SELECT policy, REALIZE voice and abstention);
  - how firmware identity appears in every trace and receipt;
  - how firmware is witnessed and rated like any other source, so outcomes feed back through the Gödel loop.
- **Already specified in Laplace-Refactor** (written from the inventor's direct requirements; this repository's AGENTS.md treats that repository as separate, so these are references to reconcile, not imports):
  - `docs/product/CONSTITUTION.md` §1. Personality Firmware is versioned executable cognition policy over the shared machine. It is not a tone prompt and not another knowledge universe. It cannot acquire authority, change truth or bypass an effect envelope.
  - `docs/product/INVENTION_MODEL.md` §12, Firmware. Firmware controls:
    - which trajectories receive computation;
    - how evidence is valued;
    - how aggressively gaps are explored and contradictions sought;
    - when uncertainty is sufficient;
    - how results are expressed.

    Personality is one class of firmware behavior. Every decision rule, parameter, tie break and trace is content-addressed and replayable. Coding firmware is a complete engineering procedure.
  - `docs/reconstruction/07_EXECUTION_CONTROL_GODEL_OODA.md`. These stay separate concerns: operation, program/recipe, orchestration, OODA (observe → orient → decide → act → observe consequence), typed feedback lanes, and Gödel extension. Gödel extension is typed incompleteness proposing a candidate calculus, program or operator, activated only on disjoint evidence.
  - `contracts/authority-stack.json`: `personality_firmware`, `governance_boundary`, `knowledge_boundary`, `creative_extension`. It also states that prompts, internal cognition and generated output create observation state but no semantic attestations merely by being observed. That conflicts with this repository's archived spec 15, where responses self-witness; reconcile.
- **Work:** reconcile those documents into this repository's binding specs, then implement firmware as content-addressed data the forward program loads, over the consolidated ISA (#951).

Related: #823, #951, #1420, #1708.

### J. Process

Issue: #1724

- **Before a push:** run `cookbook materialize` for every changed recipe (it runs the same native code the seed runs), and replay changed SQL in manifest order inside `BEGIN … ROLLBACK` on the live database. Five pipeline failures in this session would have been caught locally: a reserved column name, nested window functions, undeclared entity types, a missing gate entry, and an undeclared qualifier.
- **Before reporting a result:** look at the substrate (entity census by type and tier, `/explore`), not only claim counts.
- **Tests:** much of the suite asserts removed behaviour. Tests touched in this session were only kept compiling.
- Related: #1315, #1374, #1674.

---

## 4. Next acceptance slice

One coherent reseed on the corrected trust priors: Unicode, ISO 639-3, CILI, OEWN and OMW. Then live reads through the whole forward program, each with its trace:
- **Translation traversal:** "dog" → its English binding → i46360 → the German binding → "Hund". Also "minute", whose sense ORIENT resolves from the observation.
- capital(France)
- fire/ice

A component of that slice that fails is fixed in place. It is not replaced by a stand-in.

## 5. Decisions for the inventor

1. ~~Relation rank~~: decided, read-time only (see A).
3. **Wiktionary sense keys** when no Wikidata id exists. See H.
