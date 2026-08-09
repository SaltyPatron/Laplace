# Real conversation and heterogeneous-model consensus

This contract defines the product Laplace must expose through its MCP and
OpenAI-compatible surfaces. The running deployment and executable code own observed
behavior. Normative substrate law lives in `docs/specs/`; GitHub issues own delivery
state.

## Product claim

Laplace is a content-addressed, provenance-bearing execution substrate in which
corpora, conversations, code, games, and model circuits testify about shared entities
and relations. It is not a checkpoint merger, answer ensemble, retrieval wrapper, or
post-hoc judge.

The served result is one substrate-native forward pass over all sources selected for
the request. Source testimony converges before realization while remaining auditable,
separable, and reversible.

## Substrate primitives used by inference

The forward pass must use the same primitives as ingest and inspection:

- Unicode scalar identity at tier 0, with grapheme, lexical, phrase, sentence,
  document, conversation, and modality-specific composition above it;
- Unicode and DUCET ordering where text ordering is required, without reducing
  identity to UTF-8 bytes or one language's tokenizer;
- content hashes as entity identity, independent of source, tier, packaging, or
  tokenizer-local integer;
- S³/Super-Fibonacci placement, Hilbert ordering, and mantissa-packed trajectories as
  exact structural coordinates and occurrence manifests;
- `containers_of` and perfcache-backed point lookup for exact occurrence discovery
  across every containing trajectory;
- typed attestations with subject, relation, object, source, context, polarity,
  score, count, rating, deviation, and volatility;
- witnessing as the write path from observations and outcomes into evidence;
- consensus as the fold of compatible and conflicting testimony, never a provenance
  eraser;
- relation bands, A* pathing, hops, fan-outs, Glicko-derived confidence, and geometry
  as explicit routing dimensions;
- tiers as compositional scale, not one fixed universal notion of token.

Point distance alone is not relatedness, and approximate nearest-neighbor selection is
not the inference law. Structural coordinates identify and order substrate objects;
typed, source-bearing evidence steers the decision.

### Physicality and trajectory are different fields with different laws

A physicality's `coord` is its real PointZM placement in the shared four-dimensional
frame. A composed content entity is placed from the real PointZM placements of its
selected children, normally their centroid, and its `hilbert_index` is encoded from
that result. The `trajectory` is a lossless ordered manifest: constituent identities,
ordinals, flags, and specialized testimony may be mantissa-packed into its vertices.

Packed trajectory vertices are valid doubles by construction but are not child
placements. They must never be copied into `coord`, averaged to place a parent, or
presented as spatial coordinates. Every modality or model lane that emits a
physicality must preserve this separation, and tests must independently assert the
PointZM/Hilbert placement and the trajectory's identity/order payload.

## Conversation is a trajectory

A conversation is a content-addressed container analogous to a game trajectory. Each
turn is its own entity, with role, content, tool activity, source, context, and witness
metadata. Appending a turn creates the next conversation trajectory point without
destroying the prior prefix.

Every response is conditioned on the ordered turn trajectory and the active evidence
frontier. Topic summaries may be derived aids; they are not substitutes for the turn
record. A receipt must identify the session, prefix, resolved entities, traversed
relations, source scope, candidate frontier, selection evidence, and realized output.

The canonical forward-pass program is:

`RESOLVE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS`

- **RESOLVE** maps request content and references to substrate identities.
- **ORIENT** establishes language, modality, conversation prefix, source scope, and
  requested operation.
- **ROUTE** chooses relation bands, hop budgets, fan-outs, and admissible operations.
- **SCAN** gathers exact occurrences, trajectories, source circuits, and supporting or
  conflicting evidence.
- **COMPOSE** builds a bounded dynamic frontier from the current request and state.
- **PROPOSE** emits candidate continuations, actions, tool calls, or code edits.
- **STEER** applies typed testimony, geometry, confidence, source scope, and
  domain/tool feedback.
- **SELECT** makes one deterministic decision under the declared seed and profile.
- **REALIZE** renders the selected substrate objects into the requested protocol.
- **WITNESS** records the turn, action, result, and feedback through the governed lane.

No serving surface may replace this program with a separate recall template, static
candidate list, or endpoint-specific inference implementation.

## Model ingestion and circuit identity

Each ingested model remains an exact source. Its recipe, tokenizer, tensors, tensor
slices, layers, heads, experts, factors, modalities, and derived circuit testimony are
content-addressed and source-scoped.

Functional identity does not converge at `(plane, layer ordinal, head ordinal)`.
Ordinals are source-local addresses. Cross-model alignment is derived from the
circuit's token/entity coverage, ranked trajectory, relations, factor behavior,
geometry, and witnessed outcomes. Consequently:

- `King` from different tokenizers resolves through shared Unicode/content identity;
- a layer/head in one architecture may correlate with a differently numbered head,
  expert, factor, or non-attention circuit in another;
- TinyLlama, MiniLM, rerankers, embedding models, multimodal encoders, diffusion
  models, and other architectures do not require equal dimensions or matching layer
  layouts;
- source evidence is never collapsed merely because two local ordinals match.

Inspection must answer, with provenance:

- which layers, heads, experts, factors, and source slices touch an entity;
- how the same entity is represented across models;
- how one source circuit differs from or correlates with another;
- which source evidence supported, opposed, or abstained from a result;
- whether a pooled answer depends materially on any one source.

## Pooled heterogeneous-model consensus

Selecting sources A and B produces one forward pass, not two answers followed by a
vote, judge, concatenation, or weighted average. The pass gathers source-scoped circuit
testimony into the shared frontier, preserves contradictions, folds confidence at the
fact/circuit/action level, and realizes one answer.

The implementation must prove the distinction with A-only, B-only, and A+B ablations.
The pooled trace must contain identifiable contributions from both sources and may
produce a result not obtainable by choosing one source's completed answer. Removing a
source must remove its evidence rather than leave an unexplained cached influence.

No architecture-similarity precondition is allowed. Model compatibility is established
through shared substrate identity and functional testimony.

## Deterministic model construction and export

Export is a deterministic construction over selected substrate evidence. A request may
select one source exactly, a bounded source slice, or a pooled source set. Layer, head,
expert, factor, vocabulary, and output structures derive from attestation bands,
relation types, tiers, content, trajectories, modalities, and the declared target
recipe.

Each SafeTensors or GGUF export must carry a provenance receipt containing:

- selected sources and source slices;
- tokenizer and Unicode/content mapping;
- circuit-to-target placement and functional-alignment evidence;
- relation/band/tier inputs;
- deterministic seed and construction parameters;
- omitted or unsupported material;
- content hashes for inputs, artifact, and receipt.

Re-exporting the same substrate snapshot and recipe is byte-deterministic. Importing an
exported artifact must recover its source selection and construction receipt. The
earlier `embed=I`/lookup experiment remains evidence that substrate data can be encoded
into a conventional artifact; it is not a general architecture law or proof of
faithful conversation.

## MCP contract

The deployed MCP must:

- start through the repository launcher in a clean supported host environment;
- complete protocol initialization and tool discovery;
- expose governed reads for recall, typed query, facts, taxonomy, walks, source/model
  inspection, conversation traces, and export receipts;
- expose `witness`, `feedback`, and `ingest` only through their governed write lane;
- enforce the one-ingest-at-a-time law;
- provide one conversation operation backed by the canonical forward pass;
- return structured errors when prerequisites or capability profiles are absent;
- report the active seed/capability manifest without claiming unavailable behavior.

Tool usefulness is measured by completed workflows, not tool count. A client must be
able to inspect evidence, converse over multiple turns, generate and validate code,
compare model circuits, run pooled consensus, and retrieve receipts without bypassing
the MCP for private SQL.

## OpenAI-compatible contract

The compatibility endpoint must preserve OpenAI message roles, ordered content parts,
tool calls/results, stop conditions, streaming semantics, and supported generation
controls. Unsupported parameters return explicit errors or are omitted from declared
capabilities; they are never silently accepted.

`/v1/chat/completions` and any `/v1/responses` surface invoke the same canonical
forward pass as MCP. The protocol adapter may translate shapes but may not implement a
second inference path. Responses identify the effective model/source scope, seed
profile, finish reason, usage accounting, and trace/receipt handle.

Performance receipts distinguish substrate execution, first-result latency, and total
adapter latency. They also identify the measured unit. A `walk_text` trajectory step
rate may be reported as generated trajectory tokens per second; it is not an
autoregressive checkpoint tokenizer rate and must not be compared to one without an
explicit equivalence experiment. Read operations report rows per second, while text
readouts also report UTF-8 bytes, Unicode code points, and words so cross-surface
comparisons do not silently change units.

## Code generation as an executed conversation

Code generation uses the same turn trajectory and evidence program, with repository,
language, symbol, build, test, diagnostic, and tool-result entities added to the
frontier. A conforming loop can:

1. resolve a request against repository state and constraints;
2. propose a bounded patch or command;
3. execute authorized tools;
4. ingest compiler, test, and review outcomes as testimony;
5. revise the trajectory from that evidence;
6. return the final artifact and receipt.

Static text completion without execution feedback is not the finished code lane.

## Dataset capability manifests

Every seedable dataset declares, in machine-readable form:

- source identity, version, license, and integrity inputs;
- modalities and languages;
- emitted tiers and composition boundaries;
- emitted relation types and polarity/outcome behavior;
- trajectory/container contribution;
- attestation and witnessing behavior;
- product capabilities it enables;
- verification operations and minimum evidence expectations;
- whether it is foundational, conversational, coding, model, evaluation, or optional.

Seed profiles are capability contracts. A foundation profile must never imply real
conversation merely because Unicode and lexical sources exist. Conversation, code,
model-consensus, and export acceptance each name the profile that supplies their
required evidence.

## Behavioral acceptance

The product finish line requires all of the following:

1. A clean client initializes MCP and discovers the documented governed surface.
2. A multi-turn conversation demonstrates reference to earlier turns through the
   stored conversation trajectory, not a hand-built topic string.
3. Two identical requests under the same snapshot, source scope, profile, and seed
   produce identical decisions and receipts.
4. Changed conversation evidence or tool feedback can change the next frontier and
   result with an explainable trace.
5. MCP and OpenAI adapters produce semantically equivalent traces for the same request.
6. A code task performs at least one authorized build/test feedback cycle before its
   accepted result.
7. Source A, source B, and pooled A+B runs prove one pooled consensus pass and preserve
   source ablation.
8. Cross-architecture inspection correlates functional circuits without assuming equal
   ordinals or dimensions.
9. Source-scoped and pooled exports are deterministic and carry reversible provenance
   receipts.
10. Unicode and non-English acceptance proves identity and realization are not UTF-8-
    token or English-specific.
11. Every result exposes enough evidence to reproduce the source scope, routing,
    selection, and realization decision.
12. Evaluation rejects echo, seed-insensitive replay, source erasure, template fallback,
    and unsupported-parameter theater.

## Non-success criteria

The product is not finished when any of these substitutions is used:

- nearest-point selection as the sole relation or answer law;
- N complete model answers followed by voting or adjudication;
- checkpoint concatenation, weight averaging, or architecture-matched merging described
  as pooled consensus;
- conversation memory reduced to the latest prompt or a topic summary;
- fixed candidate frontiers unaffected by new evidence;
- a GGUF that emits plausible tokens without traceable construction and behavioral
  acceptance;
- endpoint parameters accepted but ignored;
- MCP tools that only expose diagnostics while the product path bypasses them;
- one-language or UTF-8 packaging treated as universal identity;
- foundation seed success presented as proof of conversation, code, or model consensus.

## Delivery ownership

GitHub epic [#924](https://github.com/SaltyPatron/Laplace/issues/924) owns the dependency
graph. Its work is partitioned into:

- [#920](https://github.com/SaltyPatron/Laplace/issues/920) — deployed MCP protocol proof;
- [#921](https://github.com/SaltyPatron/Laplace/issues/921) — canonical stateful dynamic-frontier forward pass;
- [#922](https://github.com/SaltyPatron/Laplace/issues/922) — OpenAI and code-lane contract;
- [#923](https://github.com/SaltyPatron/Laplace/issues/923) — source-scoped circuit comparison cube;
- [#927](https://github.com/SaltyPatron/Laplace/issues/927) — pooled heterogeneous-source consensus and ablation;
- [#928](https://github.com/SaltyPatron/Laplace/issues/928) — deterministic source-scoped and pooled export;
- [#929](https://github.com/SaltyPatron/Laplace/issues/929) — dataset capability manifests;
- [#755](https://github.com/SaltyPatron/Laplace/issues/755) — seeded end-to-end behavioral acceptance;
- [#926](https://github.com/SaltyPatron/Laplace/issues/926) — documentation and instruction authority governance.

Issue state is read from GitHub. This document defines acceptance and does not carry a
progress ledger.
