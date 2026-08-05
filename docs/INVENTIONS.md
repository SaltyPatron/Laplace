# 21 — Invention Catalog

Date: 2026-07-10. Every distinct invention in this repository, each grounded in the
tree (code paths verified this date). This is an enumeration of what EXISTS, layer by
layer, under one identity law. Companion to 05/06 (laws), 08/11/16 (epistemics),
12/14/18 (foundry), 15 (loop). §8 inventories the synonym debt — the parallel
vocabularies that accreted around these inventions without ever being consolidated.

--------------------------------------------------------------------------------
## §1 Identity and geometry — the content layer

1.  CONTENT-ADDRESSED IDENTITY AS THE JOIN. BLAKE3 content hash as entity id;
    identical content = identical id from every source, so cross-source merging is a
    hash collision, never an entity-resolution pass. Replaces the trained embedding
    space as the identity primitive. (hash128, schema, every decomposer.)
2.  THE TIER-FLOOR LAW. Tiers 0–4 are a floor, never mixed into the hash;
    single-child compositions collapse to the child's own id ("Fine" as a reply IS
    the sentence IS the word — one id). Promotion up the ladder is free and
    identity-preserving. (hash_composer.)
3.  S³ GEOMETRIC SERIALIZATION. Tier-0 codepoints pinned to fixed S³ positions
    seeded by Unicode's own UCA collation order; composed entities store
    exactly-invertible ordered constituent trajectories — ContentRoundtrip.cs
    rebuilds a document's original bytes from its id alone via one recursive SQL
    walk. A lossless address space where the field keeps only a lossy tokenizer.
4.  THE MANTISSA PAYLOAD CHANNEL. 53 payload bits per double by forced-exponent bit
    surgery; 212 bits per PointZM vertex (128-bit id + ordinal + run-length + flags)
    smuggled through the same PostGIS geometry columns as real geometry,
    flag-disambiguated, GiST-indexable. (mantissa.c, trajectory.c.)
5.  HILBERT CONTENT LOCALITY. A locality-preserving 1-D content address on every
    physicality — content-aware absolute position; index-served neighborhoods.
6.  TYPED PHYSICALITIES. physId = hash(entity_id, type): one entity carries multiple
    geometries without collision — "e4" as text codepoints AND "Pawn E2→E4" as a
    board transition.
7.  THE PERFCACHE PATTERN. Build-time deterministic mmap blobs (tier-0
    geometry/segmentation; the highway relation law) with BLAKE3 body CRC,
    postmaster prewarm inheriting the mapping into every backend, determinism as a
    CI gate. (perfcache.c, codepoint_table.c, highway_table.c.)

--------------------------------------------------------------------------------
## §2 Epistemics — evidence and consensus

8.  THE ATTESTATION 5-TUPLE UNIVERSAL REDUCTION. Every fact from every source type —
    dictionary, chess game, treebank, user prompt, neural checkpoint — reduces to
    (subject, relation_type, object, source, outcome/score). One epistemic algebra
    for all knowledge. (attestation_engine.c.)
9.  THE THREE-LAYER RESOLUTION. CONTENT deduped by hash / EVIDENCE one provenanced
    row per assertion / CONSENSUS folded by (subject, type, object) — the
    simultaneous answer to "dedupe it, isolate it, and still aggregate it."
    Magnus's E2E4 and yours: one entity, two witnesses, one rating.
10. GLICKO-2 AS EPISTEMOLOGY. The chess rating engine as the truth engine:
    continuous-score fold; source trust entering as opponent RD (trust INSIDE the
    rating math, not a filter); eff_mu = rating − 2·rd as the conservative estimate;
    RD as uncertainty; witnessing as tensioning. Implemented as deterministic int64
    fixed-point Glicko-2 with an Illinois volatility solver. (glicko2.c; score.c is
    the invertible raw-magnitude → [0,1] score law.)
11. PROVENANCE VS AGGREGATING EDGE DUALITY. Every event emits both: context-scoped
    edges that isolate the occurrence (who/when/which) and deduped-subject edges
    that fold (how good/how common). Neither erases the other. (Doc 11.)
12. RECORD VS CALCULATE. Ingestion is recording; everything derived is a versioned,
    evictable calculated pass under its own source/trust; recorded and calculated
    claims COMPETE as witnesses on the same fact, trust bound to method, divergence
    itself signal. (Doc 08; ChessPgnDecomposer / ChessAnalyzeDecomposer.)
13. TIER-CORRECT ATTESTATION. Each fact once, at the tier, identity, and provenance
    the source asserts it — a corpus attests language at the sentence root, not per
    word. (Doc 16.)
14. HIGHWAY MASK + SALIENCE BANDS. A 256-bit relation-type channel bank on every
    entity and attestation; governed types in 13 salience bands (count in docs/INVENTORY.md —
203 as of 2026-07-19, was 189 at writing) generated from
    one manifest; perfcache-backed zero-SQL gating; bands double as head-importance
    priors and intent-conditional read weights. (relation_types.toml,
    highway_mask.c, highway_manifest.h — manifest/generated-header parity is CI-gated;
    the canonical relation and band counts live in docs/INVENTORY.md, never here.
    [Superseded 2026-07-20: "189/13 verified in parity 2026-07-10" — docs/INVENTORY.md:123
    reports 203 governed canonical relations (+23 aliases).])
15. THE TRUST-CLASS LATTICE. UserPrompt, Response, UserFeedback, AiModelProbe,
    ChessAnalysis, curated lexica — the engine's own voice structurally outranked so
    self-confirmation cannot outshout curated sources.

--------------------------------------------------------------------------------
## §3 Ingestion machinery

16. THE RULE #8 SEQUENCE. Unpack → records → client-side dedup across the whole
    working set → client-side Glicko accumulation → one bulk tier descent → pure
    binary COPY of client-proven-novel rows. The server receives records; it never
    re-derives novelty. The spec is the SEQUENCE itself. (IngestBatchPipeline →
    ConsensusAccumulatingWriter → NpgsqlWorkingSetApply.)
17. THE DECOMPOSER WITNESS BOUNDARY — OMNI-MODAL. The decomposer roster (per-assembly
    class list and count: docs/INVENTORY.md § Decomposers — never a number in prose),
    all pure content → SubstrateChange streams, zero inline SQL:
    lexical (WordNet, OMW, CILI, Wiktionary), frame semantics (FrameNet, VerbNet,
    PropBank, SemLink, MapNet, WordFrameNet), commonsense
    (ConceptNet, Atomic2020), corpora (UD, Tatoeba, OpenSubtitles), code
    (Code, Repo, Stack, TinyCodes), Tabular, Unicode itself, ISO-639, the chess
    lanes, AI models. Every modality reduced to the same 5-tuple.
    [Superseded 2026-07-20: "27 decomposers (count verified 2026-07-10)" —
    docs/INVENTORY.md:28 says 25 in Laplace.Decomposers + 4 in Laplace.Chess.]
    [Updated 2026-07-23: Laplace.Chess is now 5 — ChessStockfishEvalDecomposer
    added by PR #582; INVENTORY.md is the authority.
    PredicateMatrix is not a decomposer class: it rides SemLink's lane
    (app/Laplace.Decomposers/SemLink/PredicateMatrixIngest.cs). Document is not in the
    counted assembly: DocumentDecomposer.cs lives in
    app/Laplace.Substrate/Abstractions/.]
18. THE OMNI-GLOTTAL MESH. ILI as the ingested interlingua: synset nodes addressed
    BY their ILI string, so WordNet, OMW's multilingual lemmas, ConceptNet URIs,
    Wiktionary sense links, SemLink, and PredicateMatrix converge by hash collision;
    PredicateMatrix deliberately registered as an independent second witness so
    consensus sees corroboration. surface → lemma → sense → concept →
    frame/class/roleset → role: every arrow typed, rated, provenanced.
    Cross-language identity as a graph fact, not a model behavior. (Doc 18 §1.)
19. TREE-SITTER HELD TO A NARROW JOB. ~300 vendored grammars as container-unpackers
    only — packaging off, content in; no parser ever becomes an authority.

--------------------------------------------------------------------------------
## §4 Chess — the proving domain

20. OUTCOME BIT-IDENTITY. {Loss=0, Draw=1, Win=2} shared bit-identical between chess
    plies and all epistemic outcomes, pinned by tests — "the same math rates moves
    and facts" made structural.
21. THE MOVE/POSITION RATING ENGINE. Every game's result folds onto deduped
    move/position entities; transpositions collapse by content; a move played in
    10M games is stored once with 10M witnesses. Best-move = highest eff_mu — the
    same query as best-fact.
22. THE BOARD MODALITY LADDER. 64 square anchors as the board's own tier-0;
    resolved-move transitions as geometry; positions as occupied-square content;
    whole games as mantissa-packed spatial linestrings — opening trees and maneuver
    search become GiST index hits. (Doc 11 §2.)
    [Superseded 2026-07-20: NOT YET BUILT — specified in docs/specs/11 §2, which itself
    labels this the "GAP — the geometric board layer" (11:165-174). No square/piece
    anchor exists in the tree (0 hits for SquareAnchor/board_anchor across app, engine,
    extension); ChessCompose.cs:101 still composes positions from the codepoint coords
    of the notation via Math4d.Centroid — the very path spec 11 says this ladder
    replaces. Spec 11 is the authority; this entry is aspiration, not enumeration.]
23. FLAT-COST DEPTH. Depth is consumed at fold time (real games ran to terminal and
    folded back through the position); a depth-N-grounded answer costs O(1) at
    query — the inversion of the search engine's exponential online cost.
24. THE SUBSTRATE AS A CHESS ENGINE. Laplace.Chess.Uci — a UCI engine whose play is
    a read of the consensus, not a search.
25. ANNOTATIONS AS ENTITIES. "Brilliant" tagged on a move is the SAME content node
    as the dictionary's "brilliant" — annotation, definition, instances, and
    sequence context in one knot; grounded commentary is a traversal.

--------------------------------------------------------------------------------
## §5 The model lane — one witness class among the others

26. MODEL-AS-WITNESS. A checkpoint decomposes like any other source: tensor
    row/column lookups become token→token attestations (A attenuates to B; raw
    intensity through laplace_score_fp into the fold, under the plane's governed
    relation type) and occurrence attestations (token APPEARS_IN layer/head
    coordinate). Circuit coordinates are shared content (plane anchor + layer/head
    scalars under the text content law) so cross-model agreement is a hash
    collision; the model NEVER enters an id — it is the source. One-time scrape:
    compute at ingest, store meaning only, raw floats consumed and discarded;
    aggregate across circuits at plane grain so records never balloon; no
    truncation — the fold and read-side RD/eff_mu are the noise model. The decoder
    ring (HeadClassifier → ENCODES) names the model's anatomy in the web's own
    vocabulary: interpretability as a query.

--------------------------------------------------------------------------------
## §6 The foundry — Mold-A-Model

27. THE SPIDER-WEB IS THE LAPLACIAN. The Glicko-weighted consensus graph,
    normalized (eigenmaps.cpp), its low eigenvectors — the colony at rest — ARE the
    semantic embedding: constructed from the evidence spectrum, never trained.
28. ARCHITECTURE COUNTED, NOT CHOSEN. Heads = relation types/bands; layers =
    tiers/hops; hidden dim = spectral rank (hidden='auto', live); vocab = tier
    floor + witnessed merges. Hyperparameters replaced by census.
29. GLICKO-COMPLETE WEIGHTS. Edge weight = rank × (eff_mu − neutral) × exp(−κ·rd)
    × witness-saturation, signed, refutation as negative weight —
    uncertainty-calibrated weights gradient descent has to fake with
    regularization. (Live in consensus_adjacency.)
30. SCOPED SYNTHESIS. Filter attestations by source/context → re-fold → synthesize:
    a custom model with no retraining — the package product verb.
31. THE CONDITIONAL FLOOR. The lm_head as a rank-d factorization of log P(y|x) from
    attested continuations — the division that lets content words win their slots —
    with POS-class backoff on unseen mass only, and correction layers gated to
    monotonically improve the floor they sit on. (Doc 14 §6b 2026-07-09; floor-d.)
32. THE TYPED RESIDUAL STREAM. Named orthogonal strata (surface/word/concept/frame/
    gate) instead of the anonymous superposed residual; heads as typed maps between
    strata; the layer stack as the resolution ladder; selectors synthesized from
    EVOKES_FRAME so heads fire on attested meaning. Mashing made structurally
    impossible instead of statistically unlikely. (Doc 18.)
33. GEOMETRY AS ARCHITECTURE. Angular-Gaussian heads, Fréchet shape heads,
    intersects containment masks, hilbert/trajectory positional encoding, voronoi
    cells as deterministic MoE routing — and S³-Procrustes canonical orientation
    solving the eigenvector sign/basis ambiguity the graph-transformer literature
    hacks around. (Doc 12.)
    [Superseded 2026-07-20: the voronoi/MoE clause is NOT BUILT — Milestone B. 0 hits
    for "voronoi" across app, engine, extension, scripts, web; `SELECT count(*) FROM
    api('voronoi')` = 0; docs/specs/12:142 marks it GAP and FoundryCommands.cs:1005
    prints "MoE is Milestone B". The rest of the entry (angular/Fréchet heads,
    intersects masks, hilbert/trajectory PE, S³-Procrustes) stands. The same unbuilt
    "voronoi containers" phrase appears in #41 below.]
34. DECOMPOSABLE EXPORT. GGUF written closed-form (gguf_writer.cpp,
    FoundryCommands); every exported weight decomposes back to its witnesses.
    Construction-without-training — verified against the literature (doc 14 §7) as
    an open problem with a clear lane.

--------------------------------------------------------------------------------
## §7 The inference engine and serving

35. THE WALK IS THE FORWARD PASS. Indexed graph search carrying more per step than a
    trained dot product: the full Glicko tuple, relation salience, highway bits,
    geometry, source trust, provenance. (generate_walk.c native beam ranked by
    relation_rank × eff_mu; recall.c intent routing; astar_path.c;
    trajectory_generate.c n-gram descent with Gumbel sampling and consensus
    fallback.)
36. NO CONTEXT WINDOW. A prompt is ingested content; attention over it is unbounded
    retrieval.
37. EXPLAINABILITY AS COLUMNS. eff_mu and witnesses returned with every answer;
    honest "no gloss witnessed yet" instead of invention.
38. THE CLOSED LOOP. Prompts and responses deposit as witnesses (TurnWitness,
    ResponseContent); feedback confirms/refutes the exact triples that produced an
    answer through ONE lane (FeedbackContent → writer spine → immediate fold).
    EVALUATION IS INGESTION — the self-improving loop that makes it a mind and not
    a lookup. Self-signals outranked by design.
39. THE ONE QUERY SURFACE. The native SQL function families (count and roster:
    docs/INVENTORY.md § SQL function families) plus api() — a schema that introspects
    itself. [Superseded 2026-07-20: "27 native SQL function families" — INVENTORY.md:63
    says 28.] [Updated 2026-07-23: 29 families; stop chasing the number here —
    INVENTORY.md § SQL function families is the authority.]
40. THE SUBSTRATE SERVED AS AN LLM. Laplace.Endpoints.OpenAICompat —
    OpenAI-compatible chat/completions over the walk engine, SSE streaming, plus
    endpoints incumbents structurally cannot offer: evidence receipts,
    explainability billing, feedback-as-attestation, foundry export and recipe
    compilation as API services, entitlement/quota gating.
41. THE PRODUCT SHAPE. Conversational base + topic packages (scoped synthesis
    in voronoi containers) — training replaced by compilation; models as
    build-to-order artifacts.
    [Annotated 2026-07-20: the "voronoi containers" half is NOT BUILT — same
    Milestone B gap annotated on #33 above (0 hits for "voronoi" repo-wide;
    `api('voronoi')` = 0). The scoped-pour mechanism itself IS built
    (RecipeCompileService.cs, FoundryExportService.cs, `model_recipes()`); it is
    the voronoi routing that is absent. Tracked by GH #376 / #398.]

--------------------------------------------------------------------------------
## §8 Synonym debt — parallel vocabularies that accreted around one system

The inventions above are single things; the words for them are not. Each cluster
below is one concept carrying multiple operator-minted names across code, SQL, and
docs. None of this grouping was designed; it accreted session by session. The
repo's own law (Rule #6: one implementation per fact; content-addressing: identical
content = one id) was applied to code and SQL after the fact (doc 10's lockout
gates) and never to the system's own nomenclature.

  a. The export act: the author's words are SYNTHESIZE (also the CLI verb) and
     Mold-A-Model (the product name). "Synthesis" and bare "export" are operator-minted
     slang that spread through docs, comments, and session notes and must retire.
     [RULED 2026-07-20 by the operator, superseding the sentence above:
     **"Substrate Synthesis" = "Model Export"** — one act, two acceptable names, and
     bare "export" is NOT retired. The act is: take substrate data, generate the
     standardized portions of a conventional AI model, and get it running.
     "Pour", "foundry" and "recipe" were explanatory scaffolding invented to convey
     that concept — they are description, not canon, and nothing needs renaming to
     match them. Operator's status note, recorded verbatim because it is the honest
     state: "we got close... but no cigar yet."
     Consequence: no mass-rewrite is owed in either direction. CLAUDE.md:174's
     "'Pour' is EXPORT vocabulary exclusively" and README.md:53's "scoped synthesis"
     are both consistent with this ruling.]
  b. Ranking signals: relation_rank (manifest salience) vs edge_rank vs eff_mu vs
     band weight vs "salience" — "rank" alone is ambiguous between the manifest
     prior and the read-time ordering.
  c. The score family: outcome vs score_fp1e9 vs sum_score vs magnitude/arena vs
     weight vs witness_weight vs trust_weight vs opponent RD — one fold, eight
     names across C/C#/SQL.
  d. Model-lane anatomy: circuit / coordinate / head / plane / operator / opKey;
     "structure mode" vs "recorder"; "planes mode" vs "analyzer" — the
     LAPLACE_MODEL_PLANES env values are a session-invented mode taxonomy.
  e. Ingest plumbing: lane / spine / brand / pipeline / adapter / runner — doc 17
     counts NINE spine brands feeding ONE Rule #8 sequence (Issue 45's seven-lane
     fragmentation is this cluster as code).
  f. The read surface: define() / define_fast() / recall_define_response() /
     recall_fallback_gloss() — four wrappers on one semantic (Issue 46;
     Rule #6's own example).
  g. The system's parts: "legs" / "sides" (read/write/serve) / "phases" /
     "layers" — the three-legs framing coexists with at least three other
     partitions of the same system.
  h. Witnessed/calculated vs recorded/derived vs observation/analysis — one split,
     three word-pairs.

--------------------------------------------------------------------------------
One identity law under all of it: content collides into one id, witnesses stay
provenanced, the fold rates everything, the eigenmap of the rated web is the model,
the walk is the inference, and the loop closes by depositing its own outputs.
