# 26 — Uncracked-List Campaign (2026-07-15)

Objective: empty the uncracked list. Every item below is a construction step with a
design already on record (docs 19/15/14, ledger §6/§8) or a seed lane already owned.
Empirical basis: MiniLM equivalence session 2026-07-15 (scratchpad scripts
minilm_equivalence_gate.py / head_semantics_v2.sql; results in memory
project_model_lane.md): T1 factorization lossless 7.6e-06; T2 layer-0 replay 5.1e-07;
T3 score-law roundtrip 2.3e-9 IFF arena deposited; T4 composition decay 100%→57.5%→
26.8% (L0/L1/L5); T5 factors 477× smaller than pair tiles; decoder-ring join 17×/32×
enriched vs random control.

Dependency graph: A → B → C → D → I on the critical path; E, F, G parallel lanes;
H inside B; J answered by experiment after A–D.

UNIFYING RECORD TYPE (operator, 2026-07-15): the FACTOR/PROJECTION record —
(basis-entity, subject-token, vector, source, version, arena). Bases are
first-class content entities regardless of origin: scraped circuit coordinates
(source=checkpoint) AND substrate-generated bases (consensus eigenmap bands,
relation planes, Voronoi cells; source=analysis) fill the SAME schema through
the same door (Projection physicality + FACTOR vertices + witness-context
entity). Consequences: doc-09 routing question becomes a per-circuit
measurement (self-generated vs scraped factor scoring, one query); foundry
export = read native factor records, model ingest = write foreign ones (one
table closes the loop); B' firefly index attaches to any basis; substrate
becomes one witness among models at shared consensus cells. Companion law
(doc 08 amendment owed): derivable-evidence virtualization — evidence rows
are receipts for irreproducible EVENTS; artifact-derivable assertions
(pair evaluations from factors) get a versioned derivation law + walk-grain
journal receipts; pair attestations for model planes are never materialized.

--------------------------------------------------------------------------------
A. FACTOR PAYLOADS + ARENA (§6 step 2)          [INCREMENT 1 DONE 2026-07-15:
   FACTOR vertex class live in mantissa.c/h + FactorWalk C# + 4 gates green
   (123/123 Core.Tests). Next: EmitFactorTrajectory deposit path + arena vertex
   + delete EmitBilinearPairs.]
--------------------------------------------------------------------------------
Store per-circuit factor matrices as Projection physicality trajectories on the
step-1 tensor/slice entities (ModelCheckpoint.cs — per-checkpoint byte-range ids;
no cross-model PhysicalityId collision by construction). DELETE eager
EmitBilinearPairs (ledger: rejected V²-materialization; not a fallback).

Design (doc 19 facts):
- New mantissa FACTOR vertex class: bit 0 clear, bit 6 clear, bit 7 = discriminator.
  Payload per vertex: 6×float32 = 192 bits via entity_id.lo (f0,f1), entity_id.hi
  (f2,f3), ordinal|run_length (f4), flags bits 8-39 (f5); bits 40-51 per-vertex meta
  (valid-float count). f32 exactly preserves the proven T2 replay precision.
- Per-token run inside the trajectory: 1 testimony-class header vertex (token id,
  score = salience norm → doubles as the Cauchy-Schwarz pruning bound, games = hd)
  followed by ceil(hd/6) FACTOR vertices.
- Trajectory vertex 0: testimony vertex on the circuit coordinate carrying the
  ARENA (fits ±34.36 @1e-9). Arena deposit is REQUIRED for score-law inversion (T3).
- 65,535-vertex cap → shard rows per token range; shard ordinal in physicality
  SourceDim or the header run.
- Emit path: EmitFactorTrajectory in ModelTokenEdgeETL using existing natives
  (ProjectEmbedding/SliceHead — the projections already computed today and thrown
  away). Planes: ATTENDS q/k factors; OV write vectors; MLP down-projected write
  vectors; CONTINUES_TO/SIMILAR_TO factor rows.
Gate (order: MiniLM FIRST, then TinyLlama — operator directive 2026-07-15):
1. CORRECTNESS: scrape via cli.cmd ingest model; native readback test
   reconstructs q_h(A)·k_h(B) from deposited trajectories to f32 exactness vs
   kernel-direct computation from checkpoint bytes. NO Python anywhere.
   MiniLM prerequisite: ETL must OBEY ArchitectureProfile.Bert — four verified
   defects: (a) biases never applied despite HasBiases/BiasOf(), (b) NormFold
   treats LayerNorm as column scale (no mean/var/beta; correct = compute
   x_t = LN(E[t]+P[0]+S[0]; gamma,beta) per token natively, then project),
   (c) position/segment embedding roles absent from profile, (d) WordPiece
   tokenizer path through LlamaTokenizerParser unverified.
2. PERFORMANCE (factor scrape is O(V·d^2) projection GEMMs — the V^2 tile
   deletion IS the perf fix): MiniLM (90MB) in seconds; TinyLlama (1.1B) in
   <=60s target, 120s hard ceiling, wall-clock from the seed-step log, bare
   run. Levers: MKL GEMM projections (ProjectEmbedding), layer fan-out by
   MemoryTopology/IngestTopology law (already in EmitAsync), double-buffered
   pack+stage overlapping compute, few-large-rows COPY shape. Scalar Neumaier
   qk kernels = gate reference only, never the bulk path.

--------------------------------------------------------------------------------
B. NATIVE SPI SCORER (§6 step 3)                                          [OPEN]
--------------------------------------------------------------------------------
Extension fn family: model_pair_score(circuit, tokenA, tokenB [, offset]),
model_row_topk(circuit, token, k) — SPI fetch of shard trajectory (bound params),
mantissa unpack, Neumaier dot (qk kernel math, doc 19 §5.1 — scalar variants
compile dependency-free in-extension), inverse score law via the vertex-0 arena.
Template: consensus_band_edges hybrid (blob/payload supplies operands, ONE bound
SPI query). Row top-k uses header-vertex norms for the exact CS prune.
H (RoPE) lives here: rotation of unpacked factors by relative offset before the
dot — recipe-scalar frequencies; BERT-family passes offset=NULL.
B' (optional accelerator, operator's original firefly geometry): LA+GSO+PA
projects each token's hd-dim factor to 4D (S3) per circuit, deposited as the SAME
physicality row's coord+hilbert_index (trajectory = exact payload, coord =
locality index — both channels already exist on PhysicalityRow). row_topk then
becomes Hilbert range scan (candidates) + header-norm CS prune (magnitude) +
exact factor re-score (adjudication). Borsuk-Ulam bounds the index's role:
4D nominates, never adjudicates — coord equality is never identity (existing
law). Calculated/versioned/evictable; off critical path unless B's scan profile
demands it.
MEASURED 2026-07-15 (FactorLens4dTests, native kernels, MiniLM embedding
384->k SVD, n=2000, 200 probes): k=4 gram relerr 0.91 / Spearman 0.38 /
top-20 recall 6.0% (6x chance); k=8 21%; k=16 31.8%; k=32 43.5%; no cliff.
VERDICT: native-dim storage law confirmed by measurement — 4D adjudication
impossible, 4D-alone nomination too coarse; lens pipelines want k=16-32
spectral index (or per-circuit hd=32->4 lenses — gentler squeeze, unmeasured)
+ CS norm prune + exact native re-score. Softmax-KL metric degenerate at
embedding scale — rerun on q/k circuit factors at real logit scale.
STORAGE LAW (operator): fireflies stored native-dim exact, entity->firefly
link is the record; S3 is strictly an observation lens, never load-bearing.
Gate: SQL score == kernel-direct native computation from the same checkpoint
bytes (qk kernels, engine/synthesis) ≤1e-6; row_topk == native brute force.
Gate model = one ArchitectureProfile covers (TinyLlama); NO Python instruments
anywhere in any gate (operator directive 2026-07-15 — ETL or nothing). MiniLM
enters only when the decomposer gains BERT coverage (WordPiece parser,
LayerNorm+bias fold, GELU, absolute positions) as real product code.
Constraint: extension C changes need rebuild + operator-elevated PG Start-Service.

--------------------------------------------------------------------------------
C. OODA FOLD OF WALKED PAIRS (§6 step 4)                                  [OPEN]
--------------------------------------------------------------------------------
Evaluation IS ingestion: pairs the scorer/walk touches deposit as calculated
attestations (AiModelProbe-class trust) through FeedbackContent → writer spine →
immediate fold. Consensus (A, ATTENDS, B) materializes lazily where queried; the
witnessed layer (trajectories) stays complete so nothing is truncated.
Gate: walk touches a pair → consensus row exists next query with correct trust;
cross-model second deposit collides at the same cell.

--------------------------------------------------------------------------------
D. FRONTIER-AS-RESIDUAL COMPOSITION (deep-layer replay)                   [OPEN]
--------------------------------------------------------------------------------
Layer-ordered walk carrying a sparse weighted token-mixture as the residual
stream: per layer, per circuit — softmax(scored couplings over the prompt set) →
OV mixing updates the mixture → MLP completion; unembed at the end. All reads via
B; all math native. T4 is the yardstick.
Gate: probe-set replay vs the real model — L5 top-1 attention agreement ≥90%
(from 26.8% static); next-token top-5 overlap reported honestly.

--------------------------------------------------------------------------------
E. WSD WITNESS LOOP (living priors)                                       [OPEN]
--------------------------------------------------------------------------------
Frontier-queue disambiguation (read-side plan, memory project_session_readside)
deposits (word, HAS_SENSE, sense) per corpus occurrence under the analysis
source. SemCor priors already fold (WordNetDecomposer.cs:379, verified live:
sole "only" 1163 > shoe 1126).
Gate: one corpus pass shifts witness mass onto senses (sole w≫2 distributed);
ohm→unit-not-person resolves via prior+context.

--------------------------------------------------------------------------------
F. ENCYCLOPEDIC LANE (proper names)                                       [OPEN]
--------------------------------------------------------------------------------
~40% of unknown probe words are names (alan, rihanna, nokia, poznan...). Needs a
Wikidata-shaped decomposer (new lane; license attestations owed like every seed).
Morphology half (~43%) needs NO new lane: UD lemma column + Wiktionary form-of —
reseed cargo.
Gate: probe-vocab coverage 84.7% → ≥95%.

--------------------------------------------------------------------------------
G. SENTENCE-GRAIN PROBES (positional heads)                               [OPEN]
--------------------------------------------------------------------------------
Single-token probes are blind to positional mechanisms by construction. Analyzer
pass runs corpus sentences, records per-circuit positional signatures (offset
histograms), joins against trajectory-grain web (PRECEDES/word_order).
Gate: decoder-ring coverage on heads the semantic join left silent.

--------------------------------------------------------------------------------
H. ROPE READ-SIDE ROTATION                                          [inside B]
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
I. ARGENTINA GATE (§6 step 5, acceptance)                                 [OPEN]
--------------------------------------------------------------------------------
Model-source-only token→token evidence surfaces the capital of Argentina through
the substrate's own query surface — or the miss is reported exactly as measured.
Runs after B+C (D strengthens it). This closes the list.

--------------------------------------------------------------------------------
J. DOC 09 ROUTING QUESTION                                             [OPEN — research]
--------------------------------------------------------------------------------
Does consensus × geometry × trajectory route as well as trained attention at
depth. Not built — ANSWERED: after A–D, run the foundry synthesis with factor-
informed heads and score against D's replay yardstick. Falsifiable either way.
