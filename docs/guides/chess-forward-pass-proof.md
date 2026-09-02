# Chess Forward Pass proof — one machine, one ruler, cumulative evidence

This document records the intended chess proof for Laplace. Chess is not a private engine beside the
rest of the architecture; it is a measurable modality/domain in which the same identity,
physicality, evidence, calculation, standing, ISA, firmware, search, realization, and witness laws
can be exercised end to end.

Active implementation ownership lives in GitHub issues. The main old-iteration owners are #833,
#834, #1419, and #1424; book admission is #574; the general forward-pass bridge is #1401. The clean
implementation counterparts are tracked in `Laplace-Refactor` #132/#136/#139.

## The proof is not a Stockfish clone

The intended distinction is:

```text
Stockfish 18
  strong conventional chess/search system
  external comparator
  evaluator / teacher witness
  regression opponent

Laplace
  persistent typed world
  exact canonical game/board state
  observations + books + lexical semantics + players + catalogs + calculations
  query-relative ISA / firmware / forward program
  own move selection and witness loop
```

Stockfish may propose calculations, evaluate positions, and play games. It does not own Laplace
identity, semantics, final move authority, or the architecture of the Chess Forward Pass.

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

It also has a learned piece-square projection, global and player-conditioned continuation reads,
PGN/player/game entity worlds, openings, motifs, shape/time-pressure/tablebase-style channels, a
Stockfish census lane, and a partial grandmaster-book lane.

The defect is not that none of those pieces exist. The defect is that the playing path remains
truncated: classical search is the dominant `PROPOSE`, while much of the substrate is either a
root-level steering nudge, a separate diagnostic/UI surface, or not yet composed into the same
search program.

## Material is a deterministic baseline, not the end of chess

Material is an exact calculation from board state and remains available with zero corpus. It is a
strong baseline to supplement rather than a signal to replace with popularity.

The old/crash-victim system may suggest very large gains from adding material or other terms. Those
observations are useful hypotheses. The product must measure the actual contribution under a
pinned experiment rather than hardcode folklore such as `material = +800 Elo`.

The explicit questions are:

- How much does material add?
- How much does classical placement/phase add after material?
- How much do rook files add?
- How much does pawn structure add?
- How much do motifs/geometry add?
- How much do learned structural residuals add?
- How much do PGN trajectories add?
- How much do player/opponent/time controls add?
- How much do grandmaster books add where they are applicable?
- How much does cross-modal lexical/sense orientation change behavior or strength?
- What interactions only appear when several planes are present together?

## Structural/calculated chess planes

At minimum, the chess program should be able to expose these as separate typed calculations or
query-relative projections rather than one opaque evaluation integer:

- material and material imbalance;
- phase;
- piece-square placement;
- bishop pair;
- rook open and semi-open files;
- pawn structures: doubled, isolated, connected, passed, backward/candidate passers, islands/chains
  where the recipe defines them;
- king safety / pawn shield / exposed king state;
- mobility and constrained pieces;
- space/territory under a declared definition;
- threats, hanging pieces, pins, forks, skewers, discovered attacks, mating motifs;
- outposts and weak squares;
- piece coordination, batteries, connected rooks, rook-on-seventh-style structures;
- minor/major placement and exchange structure;
- last-move/trajectory context;
- opening/LINE state;
- exact tablebase WDL/DTZ/DTB/missed-finish where available;
- transpositions and structural/geometry peers.

These calculations are not testimony. Later observations can acquire standing about how such a
structure performed without rewriting what the structure is.

## The corpus is not only PGNs

A complete Chess Forward Pass may select from several state classes at once.

### PGN/live games

Exact ordered game trajectories, participants, time/source context, moves, outcomes, and derived
position/motif occurrences.

### Player histories

Player-conditioned continuations, style/repertoire, opponent/rating/time context, and later
feature-specific standings.

### Grandmaster books

A book is ordinary document physicality first: page, paragraph, sentence, notation, diagram, and
word occurrences exist whether or not a chess extractor recognizes them. Where grounded, a book
also contributes attributed explanation/recommendation/criticism/illustration tied to positions,
moves, lines, openings, and motifs.

Book testimony never overwrites exact board calculation and ordinary prose never becomes
high-trust chess truth merely because the author is a grandmaster.

### Foundation lexical/semantic sources

WordNet, OMW, Wiktionary, dictionaries, and bridge resources already contain ordinary language used
inside chess. Chess must not mint a second private vocabulary just because a word is domain-relevant.

`fork` is the important example:

```text
lexical source        -> candidate senses/definition/taxonomy
grandmaster book      -> chess prose/explanation/variation
board calculation     -> exact fork geometry
PGNs/player history   -> observed occurrences/responses/outcomes
```

`SENSE/ORIENT` selects the chess-eligible interpretation under current context. Equal surface
content does not imply equal sense/referent.

`gambit` is equally useful:

```text
lexical meaning
+ named opening/book explanation
+ exact material sacrifice/imbalance
+ observed player/game trajectories/outcomes
```

The material deficit remains exactly negative even if other channels support the gambit under the
selected goal.

## One forward program

The chess proof should be an instance of the same generic execution machinery:

```text
RESOLVE board + language/content identities
-> SENSE ambiguous lexical/domain forms
-> ORIENT goal/player/session/authority/resources
-> SELECT admissible observation/fact/calculation/standing providers
-> SCAN exact document/game physicalities and typed evidence
-> CALCULATE board/material/structure/motif/tablebase state
-> PROPOSE legal/tactical candidate batch
-> FOLD/COMPARE only program-selected channels
-> SEARCH/UPDATE descendant states under finite resources
-> SELECT move / semantic act / typed partial or why-not
-> REALIZE/EFFECT
-> WITNESS move/result/consequence + receipt
```

A database query per alpha-beta/search node is not the intended physical implementation. The hot
path needs compiled/native/perfcache/batched state while preserving the logical typed provider
semantics.

## Stockfish 18: external ruler and teacher

The repository already pins Stockfish 18 by exact release and checksum. Treat a selected comparator
configuration as a **generation** that cannot drift during an experiment.

Stockfish has three distinct useful roles.

### Calibration opponent

`UCI_LimitStrength=true` plus a declared `UCI_Elo` can help locate a current Laplace variant. This is
coarse calibration and not a universal human-rating claim.

### Full-strength conventional comparator

`UCI_LimitStrength=false` is the external ceiling challenge. A meaningful result stamps every
calculation-affecting field, including:

```text
Stockfish release + binary digest/build target
NNUE/network identity actually loaded
Threads
Hash
SyzygyPath/tablebase boundary
search time/depth/nodes policy
other non-default UCI options
CPU/topology/affinity/concurrency
opening suite/book identity
adjudication law
```

A host-max profile may intentionally tune Stockfish to use the machine as strongly as practical.
That result is host/configuration-specific and must carry the receipt.

### Census / teacher witness

Stockfish can evaluate admitted positions/moves/lines under a named versioned calculation source.
That is useful "training" in the intended Laplace sense: calculated observations can be admitted as
one evidence provider and later compared with real games, books, players, exact tablebases, and
future outcomes.

It is not gradient training and not truth by fiat. A Stockfish calculation remains attributable to
its version/configuration. Laplace may later learn standing about where the witness is reliable or
wrong without rewriting the original evaluation.

## Freeze the ruler

Never tune Stockfish and Laplace simultaneously while claiming one Elo increment.

A measurement generation binds:

```text
Stockfish comparator generation
hardware/resource profile
opening/challenge suite
match time/depth/nodes/adjudication law
Laplace corpus/evidence epoch
Laplace firmware/recipe
```

Every cumulative Laplace variant in that generation plays the same external ruler.

If Stockfish is tuned more aggressively later, create a new comparator generation and rerun the
reference ladder. Historical measurements stay attached to the old ruler.

## Cumulative strength ladder

A useful initial ladder is:

```text
A0  legal/tactical proposal + material baseline
A1  + classical PST / phase
A2  + bishop pair / rook files / pawn structure
A3  + remaining deterministic structures / motifs / geometry
A4  + learned PST
A5  + learned structural residuals
A6  + global PGN move/trajectory evidence
A7  + player/opponent/rating/time conditioning
A8  + openings/shape/tablebase/catalog providers
A9  + grandmaster-book/expert evidence where applicable
A10 + lexical/sense/domain bridges where applicable
A11 + complete selected Chess Forward Pass
```

The exact order is a versioned experiment recipe rather than an architectural constant. New planes
can be inserted as long as the generation remains explicit.

For each rung, run two complementary measurements when feasible:

1. **Adjacent ablation** — `Ai` versus `Ai-1` under matched resources. This isolates incremental
   playing effect.
2. **Fixed external ruler** — `Ai` versus the same frozen Stockfish comparator generation. This
   shows progress on one external scale.

Also run `full` versus `full-minus-component` ablations so feature interactions are visible. A
feature may have near-zero isolated Elo yet be important in combination or under a restricted
context.

## Fair match protocol

A defensible ladder should use:

- color-swapped paired openings from an exact pinned suite;
- the same opening distribution for every variant;
- identical relevant Laplace time/depth/nodes/resource budgets when isolating behavior;
- statistically meaningful game counts;
- Elo with uncertainty/margin and/or SPRT where appropriate;
- no mid-run score promoted as final evidence;
- W-D-L, games, failures, time losses, adjudications, CPU, nodes, memory, elapsed, and provider
  identities in the receipt;
- raw PGN/transcript/config/result artifacts;
- explicit engine/source/epoch identity.

Most importantly, match games must not feed the same frozen experiment while it is running. Their
admission produces a **later evidence epoch**. Otherwise the program changes underneath the
measurement.

## Uncertainty can guide compute, not truth

Typed RD/uncertainty may guide how much physical search effort is allocated:

- exact terminal/tablebase closure can suppress speculative deepening;
- strong low-uncertainty agreement may require less confirmation;
- high-RD, novel, or contradictory states may receive more search work;
- exhaustion returns a typed partial/upper-bound/why-not state.

Low RD does not make a tactically illegal move legal and cannot override an exact mate/tablebase
constraint.

## Long-term hypothesis

The intended experiment is allowed to reach a point where a complete Laplace program beats the
pinned full-strength Stockfish comparator. That is a target to test, not a result to assert before
the data exists.

The architectural distinction still matters if that happens. Laplace can operate over persistent
typed state a conventional chess evaluator does not itself represent as one world: provenance,
grandmaster prose, lexical semantics, player-specific trajectories, historical scope, dependence,
uncertainty, and other modalities.

At that point Stockfish can remain useful as a conventional tactical/evaluation witness, regression
opponent, and historical ruler even if it is no longer the strongest platform in the experiment.

## Required receipts

A selected move should be inspectable as separate contributions, for example:

```text
move: Nf3
exact material/tactical state
deterministic positional structures
classical proposal contribution
global observed trajectory/outcome contribution
player/context-conditioned contribution
book/expert testimony where selected
lexical/sense contribution where selected
opening/tablebase/motif/geometry channels
standing/uncertainty
physical search effort
final selection/completion reason
```

Every observed/learned/expert contribution drills back to its exact evidence/provenance. Calculated
features drill back to their calculation recipe/version.

## Non-success

The following do not satisfy this proof:

- hidden Stockfish bestmove fallback;
- copying NNUE/Stockfish and calling it the Laplace Forward Pass;
- one `UCI_Elo` setting treated as the final strength ruler;
- changing Stockfish configuration between ablation rungs;
- treating Stockfish evaluation as tablebase/world truth;
- replacing material with corpus popularity;
- one permanent scalar flattening all typed chess channels;
- root-only substrate steering;
- book/WordNet state visible only in an explanation UI;
- per-search-node database calls;
- ingesting the match into the same frozen evaluation epoch;
- claiming superiority before the pinned full-strength measurement establishes it.

## Issue map

- #833 — compose the complete Chess Forward Pass;
- #834 — current matched substrate-lift protocol;
- #1419 — typed structural/observational/cross-modal evaluation planes;
- #1424 — Stockfish comparator/teacher generation and cumulative strength ladder;
- #574 — grandmaster-book admission and grounding;
- #1401 — one generic forward-pass mechanism across modalities;
- Refactor #136/#139 — clean proving slice and comparator contract.