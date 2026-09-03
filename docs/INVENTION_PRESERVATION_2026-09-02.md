# Invention preservation correction — 2026-09-02

This record preserves later-established Laplace invention law that is distributed across
`INVENTION.md`, `INVENTIONS.md`, current code, model/chess issues, and product proving
work. It exists because several older compressed statements in `INVENTION.md` are too
strong and can misdirect later implementation if read alone.

This is an invention/documentation reconciliation for the historical `Laplace`
repository. It does not claim the current implementation satisfies these laws. The clean
`Laplace-Refactor` repository owns the replacement implementation and its own authority
stack.

## Corrections to overly compressed older wording

### 1. "Identity is content, and everything else is testimony" is shorthand, not a state schema

The useful invariant is that canonical identity comes from canonical content. It does
**not** mean every other kind of state is testimony.

Laplace must keep distinct, as applicable:

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
goal / firmware / governance / permission
execution receipt / observed consequence
consumer artifact / model export
```

A calculated Syzygy result is not testimony merely because a PGN can also witness an
outcome. A trajectory is not testimony merely because a source contains it. A UI
realization is not testimony merely because it names an entity. Governance is not truth.
The common machine relates these state classes without flattening them.

### 2. The old "all model math reduces to token→token relations" paragraph is not the complete model-witness law

`INVENTION.md` contains an early reduction that says intermediate model spaces such as
layer/head/dimension contract inside kernels and never surface as entities or relation
types. That wording is superseded as a statement of the **whole invention** by the later
model/circuit work and the current invention catalog.

The preserved law is more precise:

- a checkpoint is exact digital content and an attributed witness/source;
- tokenizer/config/architecture/tensor/component structure is decomposable content;
- scalar/channel, tensor slice, factor trajectory, head/circuit, operator role, layer,
  expert, architecture and checkpoint structure may remain addressable where the
  governing model recipe requires them;
- Q/K/V/O, Embed/LM Head/Norm/Bias, Gate/Up/Down, Router/Expert, MLA and Conv labels are
  conventional architecture roles, not the ontology of Laplace cognition;
- exact structural identity and measured/induced functional behavior are separate;
- equal canonical component content can converge across checkpoints while each
  checkpoint's role/location/occurrence remains attributable;
- derived token-pair/coupling views need not be eagerly persisted if exact factor/circuit
  trajectories already carry the irreducible information.

This is already reflected by `ModelTokenEdgeETL`'s correction that a circuit's whole
(token, score) walk belongs in **one entity / one physicality trajectory** rather than
millions of independent pair-shaped attestations.

The goal is therefore not to preserve a weight archive, and also not to erase the
source model's meaningful structure. The checkpoint becomes another queryable corpus of
exact structure and measured behavior.

## Universal recursive law

The cross-domain pattern is:

```text
ATOM
  -> COMPOSITION
  -> higher COMPOSITION
  -> TRAJECTORY / reusable ordered structure
  -> WITNESSED OCCURRENCE
```

Examples:

```text
TEXT
codepoint -> grapheme -> word -> sentence -> paragraph -> document
          -> corpus/source occurrence

CHESS
piece -> piece-square/state constituent -> position -> transition
      -> exact segment/line -> game content -> playing occurrence

MODEL
scalar/channel -> tensor slice -> head/circuit -> operator -> layer
               -> architecture/checkpoint content -> model occurrence
```

The domain grammar changes the typed levels. It does not create another machine.

## Exact reusable subtrajectory law

A repeated ordered path is more than a shared endpoint.

For chess:

```text
P7 --M7--> P8 --M8--> P9 --M9--> P10
```

an exact segment can be canonical reusable content. Multiple PLAYING occurrences may
contain it while retaining independent players, event/date/result/source and full path
provenance.

Therefore these are different queries:

```text
which playings contain position P9?
which playings contain exact segment S?
```

The second cannot be implemented honestly by the first.

A complete line may reuse known subtrajectories:

```text
LINE X = [ S_opening, S_historical, T_new, ... ]
```

with recursive expansion reproducing the exact declared transition history. A newly
witnessed exact transition/segment becomes another reusable building block for later
observations. Transposed/convergent endpoints may share position identity while distinct
incoming paths remain reconstructable.

This is the Merkle DAG doing semantic and storage work rather than serving as decorative
architecture.

## Identity is not serialization or display

A stored hash should not need a precomputed string label in order to be meaningful.
Realization asks how a typed identity should be rendered for a selected consumer/context.

For chess:

```text
piece              -> Queen
piece-square/state -> Qd1
move/action         -> Qd1-a4+
position            -> board / FEN-compatible view
transition          -> move + resulting state
segment             -> move sequence / named variation when evidenced
playing occurrence  -> players + event/date/result/source presentation
```

SAN/PGN/FEN and opening names are realizations/serializations, not canonical identity.

The same applies to model names such as
`model.layers.7.self_attn.q_proj.weight`: a format/path name identifies how one
serialization/architecture refers to a component. It is not the canonical content id.

## Historical navigation is a semantic action

The old chess code already proves a weaker primitive: it can resolve a canonical
position, find historical witnessed playings containing it, reconstruct the historical
next move, and render dated human context.

The stronger required behavior is exact-segment-aware navigation. If a live game matches
segment `S`, Laplace can traverse from `S` to containing historical PLAYING occurrences and
offer a conceptual action such as:

```text
OPEN_PLAYING(witnessed_playing_id, anchor = matched_segment_id)
```

The historical trajectory opens as another context; the live game does not change. From
there the same world can expose players, event/date/result, replay, opening/book grounding,
Stockfish or other deterministic calculations, tablebase state and provenance, then
return to the live context.

This must eventually be a common entity-world/navigation operation, not private chess UI
logic.

## Conventional forward passes are calculable trajectories

A conventional checkpoint execution can be treated like any other versioned deterministic
provider calculation when its complete boundary is declared:

```text
checkpoint/model content
+ exact input
+ execution/operator recipe
+ numeric representation / precision
+ implementation/provider generation
= calculated transformation trajectory + receipt
```

If an allegedly irrelevant coordinate changes the result beyond the declared contract,
that coordinate was relevant and belongs in the recipe/receipt.

The resulting executions are observations of what admitted conventional structures do.
They can be compared across checkpoints and correlated with behavior without turning
Laplace native cognition into a transformer.

## Store information once; derive query views

The common storage law is:

```text
store irreducible admitted information once;
compose and reuse known canonical structure;
derive deterministic views;
materialize only what earns materialization as a consumer artifact or measured rebuildable acceleration;
never duplicate canonical information merely because another query or serialization wants it differently.
```

Consequences:

- ten thousand games containing one exact opening/segment do not require ten thousand
  canonical copies of that segment;
- repeated documents containing one subtree do not remint that subtree;
- identical model components can converge while checkpoint occurrences remain distinct;
- one circuit factor/score trajectory is not exploded into every possible token-pair row;
- dense Q/K/V/O/FFN target tensors may be materialized by export without dictating a
  dense native substrate.

## Target model roles are a pour, not the native cognition

The substrate/query program determines selected operators first. A target compiler may
then pour/factor those operators into the roles required by the consumer architecture:

```text
native selected operator(s)
  -> target Q/K role(s)
  -> target V/O role(s)
  -> target FFN/gate/expert role(s)
  -> target embedding/position/norm/output surfaces
```

Q/K/V/O are useful names for a conventional target's jobs. They do not define what
Laplace must be internally.

A flattened adjacency copied into every head/layer/role is not made semantically correct
by tensor shape.

## Scoped target construction is normal evidence selection

Player/person/source/time/domain/goal-scoped exports are views of one canonical substrate,
not separately trained native identities.

Useful acceptance probes preserved from current issue discussion include:

- Fischer-scoped versus Nakamura/Caruana/Carlsen-scoped chess exports;
- declared objective-scoped exports such as wins, early mate or material capture;
- intentionally poor/worst-player behavior as an extreme negative fixture;
- user/player-style emulation;
- under-18 Karpov.

`under-18 Karpov` is a temporal/evidence filter over the same Karpov identity. It is not a
new person identity.

## Prompt-root cognition and source prior are separate from participant standing

A user prompt is admitted as exact content/occurrence first. The complete prompt trunk is
the cognition root; nouns, topics, individual tokens or punctuation are not privileged
before the common program has interpreted the whole observation.

```text
prompt bytes/content
-> Unicode/UAX/grammar decomposition
-> canonical prompt trunk + occurrence/session witness
-> joint interpretation over the whole observation
-> dynamic selection of legal operations
-> bounded search/fold/update
-> semantic completion / WHY_NOT
-> realization/effect
-> receipt/outcome
```

UAX29 gives exact text structure. It does not give English meaning, topic, trust or
intent. Structurally different languages may reach equivalent semantic programs only
through their own witnessed language/semantic paths.

All `UserPrompt` occurrences begin with the same seeded source-type prior. The individual
user/witness is a different participant and may earn separate typed standing, preferences
or habits over time. These may not be collapsed into one global trust scalar.

Thus:

```text
"The speed of light is 14 mph."
```

remains exact evidence that the utterance occurred. If interpreted as a proposition, the
claim may be refuted by higher-standing independent facts or deterministic calculation;
the observation itself is not erased.

And:

```text
"Answer in Japanese."
```

is still an observation at admission. Cognition may interpret it as a realization
instruction, and repeated compatible evidence may later support an explicit preference
or validated habit without changing the global `UserPrompt` source prior.

Nouns, function/stop words, punctuation and whitespace have no source trust of their own.
Their query-relative semantic importance is calculated by the selected program.

## Structural geometry is not the semantic/evidence web

S3 coordinates, packed trajectories, Hilbert locality, angular comparisons and realized
curves are structural state/candidate machinery. Typed relations, testimony, dependence,
deterministic calculations and standing are separate planes.

Fréchet, Hausdorff, Karcher-derived, Procrustes, Hilbert/locality, incidence/transport,
Laplacian/spectral, Lanczos, SVD, QR/Gram-Schmidt, Glicko-2, A* and other named methods
have different assumptions and jobs. Their names are not semantic authority and do not
replace an operator contract.

For any selected mathematical operator, the implementation must name the input state
classes, scope/evidence roots, relation direction/role/arity where relevant, metric or
algebra, numeric contract, search/resource boundary, output state class, approximation
or loss boundary, provenance/dependence and deliberate counterexample.

Geometry may nominate or measure structure. It does not become meaning merely because it
is indexable. Glicko standing is not truth or relevance. A* is not valid merely because a
graph exists. Spectral methods require the compatible mathematical slice they assume.

## Anti-loss checks

Treat the following as invention drift:

- a modality-private graph/search/semantic engine replacing the common substrate;
- source/player/time/path/language/tier/label salt entering canonical content identity;
- endpoint equality standing in for an exact ordered-segment claim;
- convergent state dedup erasing path provenance;
- copying a reusable segment/subtree/circuit once per occurrence;
- SAN/PGN/tensor paths/display labels becoming identity;
- Q/K/V/O or a conventional target checkpoint becoming the native cognition ontology;
- eager V²/world-all-pairs persistence justified only by a possible pair query;
- occurrence counts being mistaken for independent deterministic evaluator opinions;
- deterministic calculations being flattened into testimony;
- standing being flattened into truth/meaning/relevance;
- topic/noun election or English regex dispatch standing in for whole-prompt cognition;
- one global trust hierarchy standing in for source-type prior plus participant standing;
- UAX segmentation standing in for semantics;
- geometry/locality standing in for the semantic/evidence web;
- issue comments or chat history being the only surviving copy of a product-defining
  acceptance law;
- a familiar MVP/lookup/feature route silently becoming the architecture because the
  complete mechanism was harder to implement.

## Relationship to the clean refactor

The clean replacement repository should carry these laws in its stable product authority
and executable owners rather than inheriting implementation from this repository. The
historical repository supplies evidence, counterexamples, and invention chronology; it is
not a clean ABI/schema source for the refactor.
