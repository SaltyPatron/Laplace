# Chess Forward Pass proof — one machine, one ruler, cumulative evidence

Chess is a measurable modality/domain in which Laplace can exercise the same identity,
physicality, evidence, calculation, standing, ISA, firmware, search, realization, and witness laws
used everywhere else.

Active implementation ownership lives in GitHub issues. The main old-iteration owners are #487,
#491, #574, #833, #834, #1419, and #1424; the general forward-pass bridge is #1401. Clean
counterparts are tracked in `Laplace-Refactor` #132/#136/#139.

## The three-part proving stack

The intended split is:

```text
Stockfish 18
  one strong classical calculated chess metric/provider
  external calibration/reference/full-strength opponent

Laplace
  canonical content-addressed chess world
  observations + calculations + books + lexical semantics + players + catalogs
  evolving Chess Forward Pass under test

cutechess-cli
  neutral match/tournament conductor
  paired openings + colors + clocks + process lifecycle
  PGN/transcript/result artifact recorder
```

Stockfish is not a privileged epistemic class, not Laplace's chess brain, and not a hidden final
move authority. Cute Chess is not incidental demo tooling. It is the harness that lets the ruler
stay still while Laplace variants change underneath it.

## Canonical chess state: fixed primitive scope, huge composition space

Chess is especially useful because the primitive domain is tightly bounded while the composition
space is enormous:

- 64 fixed board squares;
- fixed piece kinds/colors and legal square placements;
- bounded move representation;
- combinatorially many complete positions and game trajectories.

Laplace decomposes board/state/move structures into the same Merkle-DAG/content-addressed world as
other modalities.

The binding law is:

> **same canonical content = same BLAKE3 identity**

#491 records the explicit old-implementation ruling:

- `PositionContent` / `PositionId` is canonical substrate identity;
- Zobrist is a transposition/search accelerator only;
- source, PGN, book, analyzer, player, or occurrence cannot remint an equal position.

A game is an ordered physicality trajectory of canonical states/actions:

```text
position P0
-> move M0
-> position P1
-> move M1
-> position P2
-> ...
```

Two games may traverse the same canonical position and move while retaining distinct game/event
occurrences, provenance, players, clocks, outcomes, and trajectory ordinals.

Where chess-rule behavior depends on side-to-move, castling/en-passant or another rule-relevant
field, the canonical selected state contract must preserve it. History-dependent repetition/session
state remains event/trajectory context where appropriate rather than source salt in reusable content.

## Current behavioral starting point

The old implementation already has separable classical evaluation terms:

```text
Material
Pst
BishopPair
RookFiles
PawnStructure
Tempo
```

It also has learned piece-square projection, global/player-conditioned continuations, PGN/player/game
worlds, openings, motifs, shape/time-pressure/tablebase-style channels, a Stockfish analysis/census
lane, and a partial grandmaster-book lane.

The problem is composition, not absence: the playing path remains truncated, with classical search
owning most of `PROPOSE` while much useful substrate state remains root-only, diagnostic/UI-only, or
not yet part of one common forward program.

## Material is a deterministic baseline, not the end of chess

Material is exact calculated state and remains available with zero corpus evidence. It is a strong
baseline to supplement rather than replace with move popularity.

The crash-victim system may strongly suggest very large Elo effects from material or other terms.
Those are empirical clues to reproduce or falsify, not ontology constants.

The proving ladder is intended to measure questions such as:

- How much does material add?
- How much does PeSTO/PST add after material?
- How much do rook files add?
- Pawn structure?
- Motifs/geometry?
- Stockfish classical-analysis metadata?
- Learned structural residuals?
- PGN trajectories?
- Player/opponent/time context?
- Grandmaster books?
- Lexical/sense cross-modal state?

## Typed structural/calculation planes

A chess recipe should be able to expose these separately rather than flattening them into one
permanent opaque evaluation number:

- material and material imbalance;
- phase;
- piece-square placement;
- bishop pair;
- rook open/semi-open files;
- pawn structures: doubled, isolated, connected, passed, backward/candidate passers, islands/chains
  where defined;
- king safety / pawn shield;
- mobility and constrained pieces;
- space/territory under an explicit definition;
- threats, hanging pieces, pins, forks, skewers, discovered attacks, mating motifs;
- outposts and weak squares;
- piece coordination, batteries, connected rooks, rook-on-seventh structures;
- minor/major placement and exchanges;
- last-move/trajectory context;
- opening/LINE state;
- exact tablebase WDL/DTZ/DTB/missed-finish where selected;
- transpositions and structural/geometry peers;
- Stockfish analysis under an exact provider generation/recipe.

These calculations are not observed game outcomes. Later evidence can accumulate about how a
calculated structure/metric performed without rewriting the calculation itself.

## Stockfish is another classical metric plane

Stockfish's special value is the strength and breadth of its chess calculation, not ontological
privilege.

A Stockfish analysis is a versioned calculation over canonical chess content, in the same broad
class as PeSTO, material, rook files, pawn structure, or another classical metric provider.

For a reproducible analysis/census recipe, the logical coordinate is approximately:

```text
canonical position/state id
+ candidate move id when move-scoped
+ Stockfish generation
  - release/build/binary digest
  - NNUE/network identity
  - calculation-affecting UCI options
+ analysis recipe
  - fixed depth and/or nodes
  - searchmoves / MultiPV policy
  - selected tablebase boundary/options
  - adapter/calculation version
-> calculated result content
```

Eligible result content can include:

- centipawn/mate score;
- WDL estimate where provided;
- per-candidate move delta/quality;
- depth/seldepth/nodes;
- principal variation / MultiPV candidates;
- declared tactical/search labels derived by the adapter.

Those are classical calculated records. They remain distinct from exact tablebase facts, observed
PGN outcomes, grandmaster testimony, player history, or lexical facts.

### Same input + same deterministic recipe should converge

For the deterministic analysis/census profile:

- exact equal result content reuses/deduplicates the same semantic calculation;
- repeated execution may retain run/provenance occurrence but is not independent support;
- crash/retry must not double-count already-completed calculations (#487);
- the same position/move encountered in many PGNs/books/games can reuse the same Stockfish result;
- a different result under an allegedly deterministic closed recipe is a reproducibility discrepancy
  to surface, not a reason to remint the position or silently average the outputs.

For reproducibility conformance, use fixed binary/network/options, fixed depth/nodes, and single-thread
search where necessary to eliminate scheduling variation. Full-strength multithread/time-based match
search is a separate profile and does not have to promise bit-identical internal traces.

## Deduplication is why the analyzer belongs on canonical positions

The intended data convergence looks like:

```text
PGN game A --------\
PGN game B ---------\
grandmaster book -----> canonical position P -----> canonical move M
self-play game --------/         |                     |
                                  |                     |
                                  +---- Stockfish ------+
                                       classical metric
```

Stockfish should not generate one semantic position copy per game occurrence. The same content
converges; provenance records where it was seen; the calculation attaches to the reusable canonical
state.

That is the useful interaction between large PGN ingestion and classical analysis: real games add
independent occurrences/outcomes around canonical state while Stockfish adds reproducible calculated
metadata to that same state.

## The corpus is not only PGNs

### PGN/live games

Exact ordered game trajectories, participants, time/source context, moves, outcomes, and derived
position/motif occurrences.

### Player histories

Player-conditioned continuations, repertoire/style, opponent/rating/time context, and feature-
specific standing.

### Grandmaster books

A book is ordinary document physicality first: page, paragraph, sentence, notation, diagram, and
word occurrences exist regardless of whether chess-specific extraction recognizes them. Grounded
book material can additionally contribute attributed explanation/recommendation/criticism tied to
canonical positions, moves, lines, openings, and motifs.

Book testimony never overwrites exact board calculation.

### Foundation lexical/semantic sources

WordNet, OMW, Wiktionary, dictionaries, and bridge resources already contain language used in chess.
Chess should not mint a second private vocabulary.

`fork` is an explicit cross-modal proof:

```text
lexical source        -> candidate senses/definition/taxonomy
grandmaster book      -> chess prose/explanation/variation
board calculation     -> exact fork geometry
Stockfish             -> classical tactical/evaluation metric
PGNs/player history   -> observed occurrences/responses/outcomes
```

`gambit` is similarly useful:

```text
lexical meaning
+ opening/book explanation
+ exact material sacrifice/imbalance
+ optional Stockfish classical evaluation
+ observed player/game trajectories/outcomes
```

The material deficit remains exact even if other selected channels support the gambit under the
active program.

## One forward program

The chess proof should be an instance of the same generic machine sequence:

```text
RESOLVE board + language/content identities
-> SENSE ambiguous lexical/domain forms
-> ORIENT goal/player/session/authority/resources
-> SELECT admissible observation/fact/calculation/standing providers
-> SCAN exact document/game physicality and typed evidence
-> CALCULATE board/material/structure/motif/tablebase/Stockfish state as selected
-> PROPOSE legal/tactical candidate batch
-> FOLD/COMPARE only program-selected channels
-> SEARCH/UPDATE descendant states under finite resources
-> SELECT move / semantic act / typed partial or why-not
-> REALIZE/EFFECT
-> WITNESS move/result/consequence + receipt
```

Stockfish `bestmove` cannot secretly satisfy the final Laplace `SELECT` obligation. It is one
eligible calculated plane only when the recipe explicitly selects it.

The hot physical path should use native/batched/perfcache execution rather than one database query
per searched node.

## Distinct Stockfish profiles

### Deterministic analysis/census

Purpose: produce reproducible classical metadata over selected canonical positions/moves.

Bind exact binary/network/options plus fixed depth/nodes/searchmoves/MultiPV policy and use one thread
where required for deterministic conformance.

### Calibration opponent

`UCI_LimitStrength=true` plus an exact `UCI_Elo` setting can locate a Laplace variant coarsely. This
is a comparator control, not a universal human rating claim.

### Fixed-reference opponent

A stable resource/configuration profile used as the unchanged external ruler across the cumulative
Laplace ladder.

### Full-strength / host-max opponent

`UCI_LimitStrength=false` with Stockfish tuned to use the host strongly. Every calculation-affecting
setting and hardware/resource boundary is part of the receipt:

```text
binary/network
Threads / Hash
SyzygyPath/tablebase boundary
search time/depth/nodes law
ponder/MultiPV/other options
CPU/topology/affinity/concurrency/load controls
opening suite
adjudication law
```

This profile is the conventional ceiling challenge; its internal search need not be bit-identical.

## Cute Chess CLI is the experiment conductor

`cutechess-cli` exists to keep the comparison mechanically honest.

Under a pinned experiment generation it should own/record:

- exact engine executable/configuration identity;
- paired/color-swapped opening suite and order;
- gauntlet, round-robin, self-play, and Laplace-variant scheduling;
- clocks/time-control/depth/nodes interface as selected;
- process lifecycle and crash/time-loss handling;
- adjudication/result semantics;
- PGN/transcript/result artifact settings;
- deterministic challenge order when required by the recipe.

One harness can then answer different questions without changing architecture:

```text
Ai vs Ai-1                    what did this Laplace component add?
full vs full-minus-X          what does removing X cost / what interactions exist?
Ai vs Stockfish(reference)    how far has this rung moved on one frozen ruler?
Laplace X vs Laplace Y        which firmware/provider program is stronger here?
Laplace full vs SF host-max   ceiling challenge
self-play/regression          behavior/stability
```

The resulting PGNs are useful new witnessed physicality trajectories **after** the frozen match
closes. Ingesting them creates a later evidence epoch. The benchmark must not train itself while it
is measuring itself.

## Benchmark analysis-boundary honesty

Because Stockfish analysis can be ingested as a classical metric, every strength result must say
whether the benchmark positions/moves were already covered by that Stockfish generation.

Both modes are legitimate:

### Stockfish-informed world

The selected Stockfish calculated plane is available like any other classical metric.

### Held-out / Stockfish-blind world

The exact benchmark positions are outside the selected Stockfish census, or that provider is disabled
at inference. This tests generalization/other Laplace planes rather than direct reuse of the same
analyzer's result.

The defect is not either mode. The defect is failing to state which experiment was run.

## Frozen-ruler experiment law

One measurement generation binds:

```text
Stockfish opponent generation
Stockfish analysis generation + inclusion/holdout law
Cute Chess version + orchestration recipe
hardware/resource profile
opening/challenge suite
match time/depth/nodes/adjudication law
Laplace corpus/evidence epoch
Laplace firmware/recipe
```

Every rung uses that same boundary. If Stockfish or Cute Chess configuration is improved later,
publish a new generation and rerun the reference ladder rather than silently rewriting old Elo
claims.

## Cumulative strength ladder

A representative initial recipe is:

```text
A0  legal/tactical proposal + material baseline
A1  + classical PST / phase
A2  + bishop pair / rook files / pawn structure
A3  + remaining deterministic structures / motifs / geometry
A4  + optional Stockfish classical-analysis plane under declared scope
A5  + learned PST
A6  + learned structural residuals
A7  + global PGN move/trajectory evidence
A8  + player/opponent/rating/time conditioning
A9  + openings/shape/tablebase/catalog providers
A10 + grandmaster-book/expert evidence where applicable
A11 + lexical/sense/domain bridges where applicable
A12 + complete selected Chess Forward Pass
```

The exact sequence is a versioned experiment recipe, not permanent ontology. Stockfish analysis is
optional precisely because experiments should be able to test Laplace with and without that
classical provider.

For each rung, where feasible:

1. **Adjacent ablation** — `Ai` versus `Ai-1` under matched resources.
2. **Fixed external ruler** — `Ai` versus the same Stockfish reference generation.
3. **Full-minus-one** — complete program versus complete program with one selected plane disabled.

This separates isolated contribution, cumulative progress, and interaction effects.

## Fair match protocol

A defensible ladder should use:

- exact paired/color-swapped opening suite;
- identical opening distribution/order across compared variants;
- identical relevant Laplace resource budgets;
- statistically meaningful game counts;
- W-D-L and Elo with uncertainty/margin and/or SPRT where appropriate;
- no mid-run score promoted as final evidence;
- CPU, nodes, memory, elapsed, crashes, time losses, adjudications and provider identities;
- raw PGN/transcript/config/result artifacts;
- exact Stockfish/Cute Chess/Laplace/epoch identity.

## Uncertainty can guide compute, not truth

Typed RD/uncertainty may guide physical search effort:

- exact terminal/tablebase closure can stop speculative deepening;
- strong low-uncertainty agreement can reduce confirmation work;
- novel/high-RD/contradictory state can receive more work;
- exhaustion returns typed partial/upper-bound/why-not state.

Standing cannot make an illegal move legal and cannot override exact terminal constraints.

## Move receipt

A selected move can expose separate contributions:

```text
exact material/tactical state
deterministic structural calculations
Stockfish classical analysis when selected
classical proposal
global observed game trajectory/outcome state
player/context-conditioned state
book/expert testimony
lexical/sense state
opening/tablebase/motif/geometry state
standing/uncertainty
physical search/prune/deepen work
final selection/completion reason
```

Observed/expert contributions trace to exact evidence/provenance/dependence. Calculated features
trace to their calculation recipe/version.

## Long-term hypothesis

The experiment is allowed to show either result.

If the complete Laplace program eventually beats a pinned full-strength Stockfish generation in a
statistically defensible match, that is a measured result. If it does not, the ladder identifies
which expected gains fail or cost too much.

The point is not to reproduce Stockfish internally. Stockfish remains a strong classical metric,
analysis provider, regression opponent, and external ruler while Laplace tests whether a persistent
typed cross-modal world can become a stronger decision platform.

## Non-success

The following do not satisfy this proof:

- one analyzer-specific position copy per PGN occurrence;
- counting repeated deterministic Stockfish executions as independent votes;
- hiding reproducibility failure by averaging contradictory same-recipe outputs;
- pretending multithread/time-based full-strength search is a deterministic census recipe;
- hidden Stockfish bestmove fallback;
- copying NNUE/Stockfish and calling it the Laplace Forward Pass;
- treating `UCI_Elo=2000` as the final ruler;
- changing Stockfish/Cute Chess configuration between ablation rungs;
- treating Stockfish evaluation as tablebase/world truth;
- evaluating on previously analyzed positions while labeling the run held-out/Stockfish-blind;
- one permanent scalar flattening all typed chess channels;
- root-only substrate steering;
- book/WordNet state visible only in explanation UI;
- per-search-node database calls;
- ingesting match output into the same frozen experiment epoch;
- claiming superiority before the pinned full-strength result exists.

## Issue map

- #487 — analyzer crash/retry idempotency;
- #491 — canonical PositionContent/BLAKE3 identity, Zobrist non-identity ruling;
- #574 — grandmaster-book admission and grounding;
- #833 — complete Chess Forward Pass;
- #834 — current matched substrate-lift protocol;
- #1419 — typed structural/observational/cross-modal evaluation planes;
- #1424 — Stockfish classical metric/comparator + Cute Chess proving harness;
- #1401 — one generic forward-pass mechanism across modalities;
- Refactor #136/#139 — clean cross-modal proving slice and classical-analysis/comparator contract.
