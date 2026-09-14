# Invention preservation correction — 2026-09-02

Status: **historical reconciliation memo; superseded as authority on 2026-09-14.**

This file is retained because it records why several earlier compressed descriptions of Laplace were corrected. Its preservation laws have now been folded into the canonical authority documents:

- [`INVENTION.md`](INVENTION.md) — complete invention, proof boundaries and intended machine;
- [`INVENTIONS.md`](INVENTIONS.md) — mechanism/capability catalog;
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — architecture as currently built, including explicit divergences;
- [`../AGENTS.md`](../AGENTS.md) — implementation-agent authority/execution contract;
- [`specs/36_Laplace_Forward_Pass.md`](specs/36_Laplace_Forward_Pass.md) — coupling/orientation/forward-program contract;
- [`specs/37_Substrate_Operation_ISA.md`](specs/37_Substrate_Operation_ISA.md) — common operation ISA.

Do **not** use this dated memo to override those documents, to redirect work away from this repository, or to infer that a separate repository owns the meaning of the invention. `Laplace-Refactor` may implement/reconcile the same invention, but repository relationships do not demote the canonical law stated here.

The historically important corrections are summarized below so the reason for retaining this record remains visible.

## Identity is content is shorthand, not a one-class state schema

Canonical identity comes from canonical content under a declared recipe. It does not mean every other state class is “testimony.” Laplace keeps distinct, where applicable:

```text
canonical content identity
physicality / structural coordinates
ordered composition / trajectory / ordinal / gap state
occurrence / container placement
alias / notation / realization
external reference
observation / attributed testimony
provenance / dependence
relation law / semantic predicate
versioned deterministic calculation
typed standing / uncertainty
discourse / query state
goal / governance / permission
execution receipt / observed consequence
consumer artifact / model export
```

A deterministic calculation is not testimony merely because a source can also witness its result. A trajectory is not testimony merely because a source contains it. Governance is not truth.

## Recursive composition is the cross-domain law

The preserved pattern is:

```text
ATOM
-> COMPOSITION
-> higher COMPOSITION
-> reusable ordered TRAJECTORY / subtrajectory
-> WITNESSED OCCURRENCE
```

Text, chess, code, models and other modalities choose different typed grammars. They do not require separate identity/evidence/cognition machines.

Repeated exact substructures are canonical reusable content. Occurrences containing them retain independent provenance/path/context.

## Identity is not serialization or display

Human labels, SAN/PGN/FEN, source paths, tensor names, codec fields and other surfaces are contextual realizations/references. They do not become canonical identity merely because a consumer needs them.

Realization maps selected typed state to a requested surface after semantic selection.

## Conventional model structure is witness/source state, not native ontology

A checkpoint is exact admitted content and a source/calculation boundary. Tokenizers, tensors, layers, heads, circuits, factors, Q/K/V/O/FFN roles and measured executions may remain addressable where their source recipe requires it.

Those conventional architecture roles are useful for comparison/export and for reconstructing functional roles. They do not dictate the ontology or runtime math of Laplace cognition.

## Store information once; derive query views

The preserved storage law is:

```text
store irreducible admitted information once
-> reuse canonical compositions
-> derive deterministic views
-> materialize only measured rebuildable accelerators or required consumer artifacts
```

Occurrence volume and possible pair queries do not justify duplicate canonical content or eager world-all-pairs persistence.

## Whole-observation cognition

The current canonical correction is stronger than the 2026-09-02 wording.

A prompt/request is admitted first as one exact observation/root with constituent occurrences, discourse state and obligations. The system then computes the query-relative coupling/response field before unconstrained policy/routing is fixed:

```text
RESOLVE
-> COUPLE
-> ORIENT
-> ROUTE
-> SCAN
-> COMPOSE
-> PROPOSE
-> STEER
-> SELECT
-> REALIZE
-> WITNESS
```

Nouns, topics, individual tokens, punctuation, renderable labels or convenience relation masks are not privileged interpretation roots.

The substrate is an overlapping spider-colony web: recursive compositions, containment, occurrences, typed relations, evidence, source/context and geometric/index planes share canonical identities. The primitive is to tug the admitted structure and preserve what responds, through which route and with what typed force.

## Structural geometry is not semantic truth

The current implementation distinguishes:

```text
real physicality coordinate
packed trajectory carrier
realized child-coordinate curve
semantic/evidence relations
consensus standing
```

The current native composer uses Euclidean centroid and can place composites inside the 4-ball. Some managed paths currently use Karcher mean and keep outputs on S³; that is an as-built divergence recorded in `ARCHITECTURE.md`, not a reason to flatten all geometry into one statement.

Fréchet, Hausdorff, Hilbert/locality, Procrustes, spectral methods, Glicko-2, A* and other mathematics each require their own typed operator contract. Indexable geometry does not become meaning; standing does not become truth; A* does not become cognition merely because a graph exists.

## Universal native execution grain

A further correction established after this memo is that the same physical execution law applies to every high-volume lane, not only reads:

```text
boundary/orchestrator
-> set-sized/indexed handoff
-> native C/C++ repeated algorithmic work
-> bulk/set result + receipt
```

PostgreSQL owns durable indexed state and set access; SPI is the prepared set-sized bridge; C#/SQL orchestrate contracts/transport. Decomposition, ingestion, cognition, analysis/domain engines, reconstruction, synthesis and export must all avoid hot RBAR/per-item boundary crossings where a coarse native operator can own the same semantics.

This execution-grain law is a major part of the performance invention and is now canonical in `AGENTS.md`, `INVENTION.md`, `ARCHITECTURE.md` and the operation ISA.

## Anti-loss checks retained from this memo

Treat these patterns as invention drift unless a higher-authority document explicitly changes the law:

- modality-private identity/search/semantic engines replacing the common substrate;
- source/player/time/path/language/display salt entering canonical content identity without a declared content recipe requiring it;
- endpoint equality standing in for exact ordered-segment identity;
- convergent state dedup erasing path/occurrence provenance;
- copying reusable segments/subtrees/circuits once per occurrence;
- display names/codec paths becoming identity;
- Q/K/V/O or a witness checkpoint becoming the native cognition ontology;
- eager all-pairs persistence justified only by hypothetical queries;
- deterministic calculations flattened into attributed testimony;
- standing flattened into truth/meaning/relevance;
- noun/topic/regex dispatch standing in for whole-observation coupling/orientation;
- one global trust scalar replacing typed source/participant/evidence state;
- UAX segmentation standing in for semantics;
- geometry/locality standing in for the semantic/evidence web;
- issue comments/chat history remaining the only copy of a product-defining law;
- an MVP, familiar lookup, lowest-compute shortcut or easiest feature path silently replacing the complete architecture.

## Historical note

This document originally said the clean `Laplace-Refactor` repository owned the replacement implementation and authority stack while this repository supplied only historical evidence. That statement is no longer an authority rule. It reflected an intermediate migration/reconciliation period and could incorrectly steer later agents away from the canonical invention documents now maintained in this repository.

Use the current authority order in `AGENTS.md` instead.
