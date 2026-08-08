# Real conversation and heterogeneous-model consensus — measured finish line

**Status:** active product definition, measured 2026-08-08  
**Scope:** MCP, OpenAI-compatible serving, conversation, code generation, model
witnessing, heterogeneous-model consensus, and export  
**Authority:** the running deployment and executable code outrank this document.
Binding substrate invariants remain in specs 05/06/08/09/15/18/33/34/36/37.

This document does not inherit a smaller scope from an older plan or a closed
issue. It defines the observable result Laplace must produce and records the
measured distance from the deployed system to that result.

## 1. The claim being built

Laplace is not a checkpoint merger, an ensemble, a retrieval wrapper, or a
post-hoc answer judge. It is a content-addressed, provenance-bearing execution
substrate in which corpora, conversations, code, games, and model circuits can
testify about the same entities and relations.

For model sources, the intended operation is:

1. ingest each checkpoint as exact source content: recipe, tokenizer, tensors,
   tensor slices, layers, heads, experts, factors, and derived circuit
   testimony;
2. resolve token surfaces through the Unicode/tier/content law, so `King` from
   one tokenizer is the same content entity as `King` from another without
   requiring equal token ids, hidden widths, layer counts, or architectures;
3. preserve every source circuit as its own content-addressed tensor/head slice
   while also projecting it onto shared architectural coordinates;
4. derive functional overlap from the source circuit's token/entity/relation
   trajectory, factor behavior, geometry, and observed outcomes—not from equal
   layer/head ordinals;
5. fold compatible and conflicting testimony into one standing consensus before
   inference while retaining source, context, polarity, score, count, rating,
   deviation, and volatility;
6. execute one substrate forward pass and produce one answer; and
7. retain diagnostic source-scoped views and deterministic export without
   turning those views into an answer-selection ensemble.

The round table is therefore a **pooled model consensus**. Model A, model B,
corpora, toolchain outcomes, and conversation feedback deposit into the shared
world. They do not each generate a candidate answer. There is no runtime judge
and no N-answer picker.

## 2. What the substrate already makes possible

### 2.1 Identity, tiers, and exact occurrence

- Entity identity is content addressed. Tier is altitude/interpretation, not a
  fixed meaning of "token."
- Unicode and DUCET provide the universal text floor. S3/Super-Fibonacci/Hilbert
  placement gives content a deterministic physical plane; geometry does not
  replace semantics.
- A physicality trajectory holds lossless order. Ordinary corpus adjacency is
  not materialized as `PRECEDES` attestations.
- `containers_of(entity)` performs an indexed single-key trajectory containment
  probe. One point for `Sherlock` reaches every containing trajectory; output
  enumeration is proportional to the matches, not the corpus size.
- `geometry_successors` combines containment with ordinal unpacking to recover
  exact predecessor/successor evidence across all occurrences.

This is why the Japanese trajectory `事件のことなんだけど...` reconstructs from
individual tier-1 constituents without an English-first tokenizer. That query
proves language-general decomposition and realization; generation quality still
requires the forward-pass gate below.

### 2.2 Witnesses and consensus

- Attestations retain subject, relation type, object, source, context, outcome,
  score, and observation count.
- Consensus folds standing testimony through Glicko-2 state rather than deleting
  disagreement or treating a single probability as truth.
- Relation types and highway bands are typed execution axes. Hops, fan-out,
  geometry, masks, A* search, ordinal continuity, and Glicko state are part of
  the CPU execution model; "indexed relational execution" alone is an
  incomplete description.
- Prompt/reply witnessing already closes turns through the conversation write
  lane. Toolchain pass/fail can use the same law for code-as-player (#894).

### 2.3 Model circuit witnessing

The current SafeTensors model lane is materially beyond file metadata:

- `ModelCheckpoint` content-addresses literal tensor byte ranges and the ordered
  checkpoint root.
- `TensorRoleClassifier` recognizes embedding, unembedding, Q/K/V/O, MLP,
  norm, bias, MoE-router, expert, convolution, and related roles by witnessed
  configuration plus tensor shape/name.
- `ModelTokenEdgeETL` computes token projections from the actual weights and
  emits per-head attention salience, per-head OV write magnitude, MLP activation
  magnitude, and MoE expert-routing testimony.
- Each circuit carries a full ranked token testimony trajectory plus a bounded
  attestation prefix under `ATTENDS`, `OV_RELATES`, or `COMPLETES_TO`.
- Factor trajectories retain exact tensor-slice provenance. Model source remains
  on the evidence even where content entities converge.
- Existing source-scoped consensus can isolate one model or a selected source
  set for diagnostics and export.

Relevant implementation:

- `app/Laplace.Decomposers/Model/ModelCheckpoint.cs`
- `app/Laplace.Decomposers/Model/TensorRoleClassifier.cs`
- `app/Laplace.Decomposers/Model/ModelTokenEdgeETL.cs`
- `app/Laplace.Decomposers/Model/ModelCoordinates.cs`
- `app/Laplace.Cli/FoundryCommands.cs`

### 2.4 CPU-native construction and execution

- The repository contains no CUDA, HIP, or OpenCL source/build lane. Heavy math
  is native CPU work through MKL/TBB/CBLAS/LAPACK and PostgreSQL extensions.
- The measured Linux host has no visible VGA/3D PCI device, `/dev/dri`, or
  `/dev/nvidia`; no GPU driver is loaded.
- The June FAITHFUL experiment wrote a GGUF from substrate-derived lookup
  structure in about 0.1 seconds and llama.cpp produced non-empty semantic
  continuations. It is evidence that constructed weights execute; it is not yet
  proof of the complete typed-consensus model.
- Foundry already maps relation, metric, trajectory, sentence-order, context,
  and conditional planes into transformer tensor slots and can source-scope a
  synthesis run.

## 3. Measured deployed baseline (2026-08-08)

The first localhost failure in this audit was sandbox-network isolation, not a
dead database. Host-level verification found:

| Surface | Measured state |
|---|---|
| PostgreSQL | 18.3 active under `laplace-postgresql.service`, port 5432 |
| Extensions | PostGIS 3.6.3, `laplace_geom`, `laplace_substrate` |
| API | `laplace-api.service` active; readiness HTTP 200 on port 8080 |
| Health | `substrate_health().ok = true`; perfcache ready |
| Entities | approximately 4.34 million |
| Physicalities | approximately 4.34 million |
| Attestations | approximately 6.63 million |
| Consensus rows | approximately 5.99 million |
| Live source roster | Unicode, ISO639, CILI, WordNet, VerbNet, PropBank, FrameNet, MapNet, WordFrameNet, SemLink, PredicateMatrix |

The deployed source roster does **not** currently prove that conversational
corpora, code corpora, or model checkpoints have been ingested on this instance.
Repository capability and live seeded capability must not be conflated.

### 3.1 Local model test material

`/data/models` on the Linux host is approximately 326 GB. The verified
checkpoint set spans incompatible architectures and tasks: TinyLlama, Phi-2,
DeepSeek Coder 33B, Qwen dense/MoE coder models, MiniLM/BERT embeddings, Jina and
Qwen embedding/reranking models, Qwen vision-language embedding/reranking, and
DETR/RT-DETR/Grounding-DINO vision models. Larger checkpoints on the Windows
machine are outside this host's measured inventory.

The immediate acceptance matrix therefore already includes different
vocabularies, hidden widths, layer/head counts, dense/MoE, encoder/decoder,
multimodal, and object-detection families. Architecture-compatible weight
merging is not an acceptable substitute.

## 4. Current blockers, with evidence

### B0. The deployed MCP launcher is broken

`/opt/laplace/app/laplace-mcp` resolves to
`mcp-runtime/Laplace.Endpoints.Mcp`, but that target is absent on the measured
deployment. An older executable exists under `/opt/laplace/app/mcp/`. The
repository deploy script now stages `mcp-runtime` and contains an EOF smoke
test, but the live installation does not satisfy that contract.

Until the canonical launcher passes `initialize`, `tools/list`, and a read-only
`tools/call`, the MCP is not a usable product surface regardless of its source
catalog.

### B1. MCP has useful tools but does not yet expose one finished product

The source MCP publishes 22 tools, including recall/query/taxonomy/walk/infer,
health/source status, generic catalogued `op`, governed witness/feedback/ingest,
and operator pipeline functions. This is a strong substrate console, but:

- product conversation and operator/ingest controls share one undifferentiated
  tool catalog;
- `chat` describes walk-driven prose although default `laplace.chat()` does not
  select the walk branch;
- MCP and OpenAI clients can reach different generation paths; and
- deployment/version visibility is insufficient when a long-lived stdio client
  retains an old tool catalog (#811).

### B2. OpenAI-compatible chat defaults to recall/template behavior

`/v1/chat/completions` routes `laplace-converse-001` through
`NpgsqlSubstrateReads.ChatAsync`. The OpenAI request has no shape field, so
`laplace.chat()` takes its default describe/fallback branch. Only an explicit
MCP `shape: "walk"` reaches `converse_compose`.

The endpoint's current contract also:

- chooses the last non-empty message regardless of `system`, `developer`,
  `user`, or `assistant` role;
- intentionally ignores resent history and retains only substrate-side session
  state;
- accepts several generation controls that are unused or only partially used;
- parses stop sequences but does not apply them to generation;
- labels `laplace-code-001` separately while sending it through the same
  `walk_text` lane as generic completion; and
- has no `/v1/responses`, tool-call, or structured-output execution contract.

Compatibility must mean behavior or an explicit unsupported-parameter error,
not merely accepting the JSON field.

### B3. Conversation state is a topic summary, not a turn trajectory

Prompt and response content is witnessed, but live orientation still depends on
the UNLOGGED `session_topics` stopgap. `converse.session_trajectory()` groups by
resolved topic and returns `(topic_id, last_ord, mentions)`. It does not return
the ordered messages, roles, sentence/code/tool constituents, corrections, or
reply dependencies of the conversation.

A session must be a stable identity pointing to a versioned ordered trajectory;
each turn is a move and each message is itself a tiered trajectory. #360 owns
retiring the stopgap.

### B4. Generation is fragmented and the frontier is static

There are multiple independent walk/generation engines (#354). The two paths
relevant to product text also split semantics:

- `converse_compose` builds a semantic frontier once, builds a weighted stream,
  then walks it;
- `walk_text` initializes from prompt content and calls
  `walk_continuations` independently.

Neither path demonstrates a single canonical state transition in which every
emitted constituent updates the frontier/residual state before the next
selection. A prompt-conditioned prior is not token-by-token attention.

### B5. Structural coordinates and functional circuit comparison are not yet a complete read model

The model lane correctly has two identities:

- `ModelCoordinates.CoordinateId` is a shared architectural square composed from
  functional plane plus layer/head/expert ordinals. Qwen L5.H7 and TinyLlama
  L5.H7 meet there so a caller can ask how the same structural address differs
  by source.
- The exact tensor/head slice is the source circuit. It is content addressed,
  linked to the shared coordinate by a source-attributed `APPEARS_IN` witness,
  and carries its own factor trajectory/projection physicality.

Sharing an ordinal coordinate is therefore a comparison axis, not a claim that
the circuits are functionally equivalent. Functional correspondence such as
Qwen L5.H7 ↔ TinyLlama L12.H3 should be derived from source-slice evidence:
ranked entity testimony, salience, factors, trajectory shape, spatial proximity,
relation plane, and observed behavior.

The storage ingredients exist, but the typed comparative surfaces are
incomplete. The product must answer without ad-hoc SQL:

- which layers/heads/experts in each model contain or strongly touch `King`;
- how L5.H7 differs across model sources;
- which differently addressed circuits correlate across sources and why;
- whether a correlation is structural, geometric, token-functional, behavioral,
  or supported across several axes; and
- which evidence and source observations produced the comparison.

Two concrete storage/read defects must be resolved:

- default `structure` mode writes the full ranked circuit trajectory as a
  Projection physicality on the shared coordinate entity. Physicality identity
  is `(entity_id, type)`, so a later model at the same structural coordinate has
  the same physicality id and its full trajectory is skipped. Its bounded
  source-attributed testimony remains, and the separate `factors` analyzer does
  correctly put full trajectories on content-addressed tensor slices, but the
  default structure record cannot by itself retain every source observation;
- `model_attention_row` is not source-scoped and can combine Q/K slices from
  different models at one shared coordinate, while `model_forward` correctly
  requires `p_source`.

No single installed operation exposes the complete source × plane ×
layer/head/expert × entity comparison cube. This read-model gap—not shared
coordinates themselves—is the central bridge from model audit to model
consensus.

### B6. Model coverage is broad in classification, narrow in runnable planes

`ModelConfigReader` recognizes text, vision, audio, and diffusion modalities,
and `TensorRoleClassifier` records unknown/convolutional tensors. Full numeric
circuit extraction is currently gated to text-anchored configurations with a
classified embedding table. Vision/audio/diffusion are partial metadata or
embedding coverage, not parity with Q/K/V/O/MLP text extraction.

Architecture honesty issues already exist under #793, #384, #383, and related
model-lane tickets. They are no longer safely "deferred" if heterogeneous model
consensus is part of the product claim.

### B7. The pooled model consensus is not yet the served forward pass

Model attestations and source-scoped consensus exist, but the OpenAI/MCP
generation paths do not execute a demonstrated pooled model-circuit program.
`model_forward` is explicitly unverified (#488); deep-layer replay and the OODA
fold remain open (#368–#370). Foundry exports substrate planes, but live chat is
not an exported GGUF and does not load one.

### B8. Export proves construction, not yet faithful reversible behavior

The production export found in-tree is GGUF/Llama-shaped. Export metadata and
tensor naming remain partly hardcoded (#272), typed residual strata and
tier-scheduled operators remain open (#521/#515), and semantic behavioral gates
against a reference remain undefined or unproved (#475/#476/#8/#112).

"Clean export" must be stated precisely: Laplace can deterministically construct
a new model from witnessed source-scoped substrate state without optimizer state
or new gradient training. It cannot claim that ingest magically removes all
biases or errors learned by the source checkpoint; those remain testimony to be
corroborated or contradicted.

### B9. Current evaluation does not test the product claim

The current probe file mainly tests election/routing hygiene. HTTP generation is
deferred; it does not gate multi-turn instruction following, correction,
anaphora, code compile/test, model-source consensus, source ablation, or exported
runtime behavior. #755 was closed even though the substantive seeded quality
acceptance remains unmet.

## 5. Public comparison — what is and is not close

| Work | Relevant capability | Why it is not the Laplace operation |
|---|---|---|
| MergeKit / TIES / PEFT merging | Produces one checkpoint from compatible tensors/adapters | Parameter arithmetic/layer assembly; no semantic circuit identity, evidence graph, standing consensus, or behavioral provenance |
| FuseLLM / FuseChat | Transfers distributions from heterogeneous LMs into one target | Requires continual training/SFT/DPO and a chosen target architecture; no source-queryable circuit consensus |
| PackLLM | Produces one token stream from arbitrary models at test time | Runs every source model and combines probability distributions; runtime ensemble, not one pre-folded substrate pass |
| Cross-model crosscoders | Learns shared/model-exclusive features across models and, recently, different architectures | Auxiliary autoencoder training, reconstruction error/polysemanticity, analysis/diff output; no consensus execution or export |
| Mechanistic universality/circuit studies | Compares features and task circuits across architectures | Research measurement on selected behaviors; no persistent multi-source world or unified inference engine |
| Reasoning/answer consensus systems | Aggregates several generated traces/answers | Post-query ensemble/judge; Laplace requires consensus before generation and one answer path |

Primary references:

- <https://github.com/arcee-ai/mergekit>
- <https://arxiv.org/abs/2401.10491> (FuseLLM)
- <https://arxiv.org/abs/2404.11531> (PackLLM)
- <https://transformer-circuits.pub/2024/crosscoders/index.html>
- <https://arxiv.org/abs/2602.11729> (cross-architecture crosscoders)
- <https://arxiv.org/abs/2410.06672> (mechanistic universality)

No public system found in this audit satisfies all of: heterogeneous checkpoint
circuit extraction, content-level semantic alignment, source-preserving
attestation, uncertainty-bearing standing consensus, one CPU-native forward
pass, and source-scoped deterministic export. This is a research result, not a
claim about private/unpublished systems or a patent freedom-to-operate opinion.

## 6. Product finish line

Laplace reaches the finish line only when all gates below pass on a clean,
declared seed profile.

### F1. Operational MCP

- The canonical deployed launcher exists and passes JSON-RPC initialize,
  `tools/list`, and representative read/write-lane calls.
- The server reports build/version/source roster and whether a client catalog is
  stale.
- Product tools are discoverable without exposing operator controls as the
  normal conversation interface.
- MCP `chat` and HTTP chat invoke the same canonical program with the same
  defaults and provenance.

### F2. One canonical stateful forward pass

One implementation owns the loop:

`resolve → elect → type/band route → scan/contain → A*/hop/fan-out → sequence →
score → select → update frontier/session → realize → witness`

Every emitted constituent updates the active state. Relation types, tiers,
Glicko confidence, exact occurrence evidence, trajectory ordinal, geometry,
highway masks, and source policy are observable contributors. Any remaining
specialized kernels are opcode implementations behind this program, not rival
product engines.

### F3. Real conversation

A held-out multi-turn session must demonstrate:

- system/developer/user/assistant role semantics;
- exact prior-turn recall through the session trajectory;
- correction of a prior claim without deleting the old witness;
- pronoun/anaphora resolution across turns;
- topic return after a digression;
- explicit abstention when support is absent;
- streaming and non-streaming semantic equality; and
- complete prompt/reply/tool provenance.

### F4. Real code generation

Using the code trajectory/AST lane, Laplace generates a new bounded program,
stages it as content, invokes the declared compiler/test runner, witnesses
success or failure with diagnostics, folds the outcome, and measurably changes a
subsequent generation/ranking. The endpoint label `laplace-code-001` must select
this program, not generic text continuation. #894 owns the closed loop.

### F5. Functional circuit consensus across heterogeneous sources

Use at least three incompatible local checkpoints (initial recommendation:
TinyLlama causal LM, MiniLM/BERT embedding model, and Qwen reranker or MoE):

- ingest exact source manifests and circuit/factor testimony;
- prove shared surface entities converge despite tokenizer differences;
- prove source circuits retain distinct slice identity while sharing comparable
  architectural coordinates;
- answer `King → all source circuits` and one same-coordinate source-difference
  query through typed operations;
- prove every source's full circuit observation survives at a shared coordinate
  and that attention/factor reads never cross-pair slices from different sources;
- derive at least one functional correspondence where ordinal coordinates
  differ and reject at least one false functional match at the same ordinal;
- show agreement increases confidence and contradiction remains inspectable;
- query the pooled state once and receive one answer;
- rerun with A-only, B-only, and A+B source scopes for diagnostics, not runtime
  candidate selection; and
- show the A+B answer/trace includes evidence unavailable in either isolated
  scope while preserving each contribution's provenance.

### F6. Exact relation support

For prompts such as "the capital of France is", the typed relation operator must
produce the supported object set from evidence (`Paris` for the positive
CAPITAL_OF example), not an ANN neighborhood. Where multiple objects are valid,
the result remains a witnessed distribution until the declared selection step.
Softmax probability outside the supported set is not evidence.

### F7. Deterministic source-scoped export

- Unicode/ISO + one model source exports a deterministic clean construction of
  that source's witnessed behavior.
- A+B exports the pooled consensus program, not concatenated tensors or an MoE
  router over the original models.
- The output loads in the declared external runtime and passes semantic probes.
- Repeated exports from identical substrate state are byte-identical or explain
  every permitted nondeterministic field.
- Export includes a machine-readable receipt linking tensors/operators back to
  consensus cells and evidence sources.

### F8. Honest OpenAI compatibility

- Supported roles and parameters affect behavior and are regression-tested.
- Unsupported fields return an explicit contract error.
- Chat/completions/code/MCP parity is tested at the semantic trace level.
- `/v1/responses` and tool calling are either implemented for agent/code loops or
  explicitly absent from the advertised capability surface.
- No external LLM may supply the answer in the acceptance environment.

### F9. Seed profiles and expected capability

- **Foundation:** Unicode/ISO and core identity/geometry. It can decompose,
  resolve, contain, and render; it is not claimed to converse fluently.
- **Knowledge:** lexical/semantic/frame sources. It can answer supported typed
  facts and relations.
- **Discourse:** sentence/document/dialogue trajectories. It supplies fluency,
  continuation, and conversational patterns.
- **Code:** repository/grammar/toolchain testimony. It activates code generation
  and verification.
- **Model:** checkpoint circuit testimony. It activates model replay, functional
  consensus, and scoped export.

More data should improve coverage and wisdom; it must not be used to excuse a
broken forward pass. Every profile has a declared capability floor and held-out
gate.

## 7. Non-success criteria

None of the following closes the product epic:

- non-empty or locally grammatical text;
- template/gloss recall presented as conversation;
- a single hand-selected prompt that works;
- a topic-frequency biography presented as session memory;
- N model answers followed by voting, judging, or reranking;
- tensor averaging, layer concatenation, Frankenmerge, or MoE routing;
- matching circuits solely because layer/head ordinals match;
- a GGUF file that has not loaded and generated in an external runtime;
- accepted API parameters that do not alter behavior;
- election-only probes with HTTP generation deferred; or
- using an external trained model to phrase the final response.

## 8. Dependency-ordered execution

1. **Repair and prove the deployed MCP.** This is the shortest route to a real
   external client and removes an operational false negative.
2. **Install a product eval gate.** Complete reopened #755 with seeded HTTP/MCP,
   multi-turn, trace, latency, and negative controls before further generation
   changes.
3. **Consolidate the speaking loop.** Expand #354 from a design-only outcome to
   one canonical stateful forward-pass implementation; incorporate #350,
   #756, and #757 rather than duplicating them.
4. **Make the session a trajectory.** Complete #360 and consume the ordered
   turns in the canonical state update.
5. **Expose the model comparison cube.** Keep shared structural coordinates and
   content-addressed source slices; add typed inverse, source-diff, and
   cross-ordinal correlation operations over witnessed trajectories/geometry;
   wire their results into #368–#370 and the fold.
6. **Prove pooled model consensus.** Run the heterogeneous three-source gate
   before claiming round-table behavior.
7. **Activate code-as-player.** Complete #894 on the same forward/witness loop.
8. **Finish export round-trip.** Close #8/#111/#112/#475/#476 with external
   load, semantic probes, provenance receipt, and pooled/source-scoped exports.
9. **Run the public demonstration.** One script, clean profile declaration, no
   manual SQL, no external LLM, no GPU requirement: MCP conversation, correction,
   code generation/test feedback, heterogeneous pooled answer, trace, export,
   external runtime generation.

## 9. Issue ownership rule

Existing implementation issues remain owners where their acceptance matches
this document. Closed issues do not erase unmet behavioral acceptance. New
issues should cover only gaps with no current owner: deployed MCP viability,
canonical product forward-pass delivery, role/parameter contract parity, typed
cross-architecture circuit comparison/correlation, and the product-level epic.

### Issues created/reconciled by this audit

| Issue | Owner |
|---|---|
| #924 | Parent epic: real conversation, code, pooled heterogeneous-model consensus, export |
| #920 | Deployed MCP launcher and live JSON-RPC protocol proof |
| #921 | One stateful dynamic-frontier forward pass shared by MCP/OpenAI |
| #922 | Honest OpenAI role/parameter/tool/code contract |
| #923 | Source-scoped circuit comparison cube and cross-architecture correlation |
| #755 | Reopened product evaluation gate; prior scaffolding did not meet behavioral acceptance |

The parent epic also binds existing owners #350, #354, #360, #368–#370,
#756–#757, #811, #894, #8, #111–#112, #475–#476, and #488 into one final
demonstration without duplicating their implementation scope.
