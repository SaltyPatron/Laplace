# Chess Forward Pass proof — one machine, one ruler, cumulative evidence

Chess is a proving domain for the same identity, recursive physicality, trajectory, evidence, calculation, standing, coupling, sparse execution, realization and witnessing laws used everywhere else in Laplace.

This guide is a measurement/acceptance design. It does not define a chess-private intelligence stack and it does not transfer authority to a “clean counterpart” repository. Current implementation ownership lives in current GitHub issues; related Refactor issues are coordination/evidence only.

## Proving stack

A useful controlled split is:

```text
Stockfish (exact source revision and build recorded for each experiment)
  strong external classical calculation provider / comparator / opponent

Laplace
  canonical chess content + trajectories + observations + calculations + books +
  lexical/semantic bridges + player/game evidence + one common forward program

cutechess-cli
  neutral experiment conductor
  paired openings/colors/clocks/process lifecycle/result artifacts
```

Stockfish is not a privileged epistemic class or hidden final move authority. Cute Chess is the conductor that keeps the experimental ruler fixed while Laplace variants/providers change.

## Canonical chess content and occurrence

Chess has a tightly bounded primitive/rule vocabulary and a huge finite-composition space: board squares, pieces/states, moves, positions, lines and games form exact typed structures in the same recursive substrate as other modalities.

The identity law is:

> **Same canonical chess content under the same declared recipe converges on the same executable identity.**

Current executable identity is a finite Hash128/BLAKE3-derived implementation choice. It is not the abstract content itself and it is not a proof of global mathematical injectivity.

Zobrist or other chess-search keys are accelerators, not canonical substrate identity. PGN source, book, player, analyzer, worker, batch or occurrence may not remint an equal canonical chess state merely because it was encountered elsewhere.

A game is an ordered trajectory of reusable states/actions plus a distinct witnessed occurrence:

```text
P0 -> M0 -> P1 -> M1 -> P2 -> ...
```

Two games can traverse the same canonical position/move while retaining different event/game provenance, participants, clocks, source, result and trajectory context.

Rule-relevant state such as side-to-move, castling rights, en-passant or other selected position semantics belongs in the declared canonical position recipe. History-dependent game/session state remains trajectory/occurrence context where appropriate rather than source salt.

## Geometry and trajectories obey the common law

Chess does not get a private coordinate system.

- `coord` is the real physical placement under the selected physicality recipe;
- packed trajectory vertices are exact constituent-id/order/flag carriers, not board-state positions;
- realized trajectory geometry resolves child ids to their actual coordinates;
- native centroid composition and any current managed Karcher paths must be reconciled under the common architecture rather than silently treated as equivalent.

Do not use packed carrier doubles as semantic/spatial chess geometry.

## Deterministic calculation planes

Material and other exact/classical board calculations are calculations, not observed game testimony.

Useful separable planes may include:

```text
material / imbalance / phase
piece-square placement
bishop pair / rook files / pawn structure
king safety / mobility / space
motifs: pins, forks, skewers, discovered attacks, mating patterns
outposts / weak squares / coordination
opening / line / last-move trajectory state
tablebase WDL/DTZ/DTB where selected
structural / geometric peers
Stockfish calculation under exact provider generation/recipe
```

Later game/book/player evidence may accumulate around those calculated structures without rewriting the calculation itself.

## Stockfish is a versioned calculation provider

A reproducible Stockfish analysis is approximately keyed by:

```text
canonical position/state id
+ candidate move/transition id when move-scoped
+ Stockfish generation
    binary/build digest
    NNUE/network identity
    calculation-affecting UCI options
+ analysis recipe
    fixed depth/nodes and/or declared work law
    SearchMoves / MultiPV policy
    selected tablebase/options boundary
    adapter/calculation recipe version
-> calculated result content
```

Possible results include cp/mate/WDL, candidate deltas, PV/MultiPV, depth/seldepth/nodes and declared derived labels.

Repeated execution of the same deterministic closed recipe may produce run/provenance occurrences; it does not create independent semantic witnesses merely because the position appeared in many games/lines.

A deterministic discrepancy is a provider/reproducibility health failure to surface, not a reason to remint the chess position or count both results as corroborating independent testimony.

## Dedup calculation, preserve occurrences

The intended convergence is:

```text
PGN A --------\
PGN B ---------\
book ------------> canonical position P ----> canonical move M
self-play -------/          |                       |
                            +------ Stockfish ------+
                                  one calculation generation/result
```

Ten thousand independent game occurrences can legitimately contribute ten thousand observed game contexts/outcomes. They do **not** create ten thousand independent Stockfish opinions for one identical calculation generation/result.

Calculation occurrence/provenance may record which runs/games triggered or consumed a calculation without multiplying its semantic support.

## Cross-modal evidence is allowed because identity is shared

A proving domain should exercise the web rather than stay isolated.

For example `fork` can connect:

```text
lexical/sense sources
+ grandmaster prose/book evidence
+ exact board motif calculation
+ Stockfish classical calculation
+ PGN/player/game occurrences/outcomes
```

Likewise a gambit can combine lexical meaning, opening/book explanation, exact material sacrifice/imbalance, optional classical analysis and observed game/player trajectories.

These are typed routes through one substrate. No channel gets to erase the others into one permanent opaque score.

## Chess uses the canonical forward program

Chess is an instance of:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

### RESOLVE

Admit exact board/game/request content, constituent state, active player/session/context, world/source boundary, hard caller constraints and obligations.

### COUPLE

Tug every eligible chess and cross-modal plane under the boundary: legal/structural state, calculations, games/players/books, lexical/sense state, openings/motifs/tablebases, standing/uncertainty, source/dependence, trajectory/geometry and selected external providers.

Preserve typed response routes. Do not choose one move/meaning/provider first and then search only evidence compatible with that choice.

### ORIENT / ROUTE

Determine the actual task—play, explain, analyze, compare, find line, prove tactic, etc.—and compile eligible providers/operators plus hop/fanout/search/resource bounds.

Stockfish may be selected as one classical provider. Its `bestmove` cannot secretly satisfy Laplace's final `SELECT` obligation unless the explicit operation is literally “return Stockfish bestmove.”

### SCAN / COMPOSE / PROPOSE / STEER / SELECT

Use sparse indexed star expansion and native board/search operators over the admitted workset. Legal move generation/proposal, alpha-beta/A*/best-first/trajectory/tablebase/classical providers are operators in the routed program, not replacements for the whole cognition path.

Standing/uncertainty may guide work allocation but cannot make an illegal move legal or override exact terminal state.

### REALIZE / WITNESS

Render the selected move/analysis/semantic act into SAN/UCI/JSON/UI as requested, then witness game/action/result/calculation consequences when the operation contract calls for it.

Each emitted move changes the active board/game trajectory and therefore the next coupling/frontier.

## Native execution grain matters to the proof

A correct chess algorithm wrapped in one database/native boundary per search node is not a conforming performance architecture.

The hot path should look like:

```text
bounded indexed state fetch / prepared SPI
-> coarse native C/C++ board/search/frontier operator
-> native loops + batch proposal/evaluation
-> bounded result + receipt
```

Avoid per-node P/Invoke/SPI/SQL, recursive CTE search, one-row candidate queries, per-position temp tables and scalar-call loops disguised as batches.

This is why an old consumer CPU can provide meaningful proof: the architecture should avoid both unnecessary world work and unnecessary boundary overhead before wider SIMD/GPU headroom is credited.

## Stockfish experiment profiles

Keep distinct profiles separate.

### Deterministic analysis/census

Exact binary/network/options plus fixed work recipe; single-thread where required to establish deterministic conformance. Produces reusable calculated state.

### Calibration opponent

A pinned limited-strength Stockfish configuration can roughly locate a Laplace variant. It is comparator evidence, not a universal human Elo claim.

### Fixed-reference opponent

One frozen Stockfish generation/resource recipe used across a cumulative Laplace ladder so the external ruler does not move.

### Host-max/full-strength opponent

Pinned binary/network/Threads/Hash/tablebase/work/time/adjudication/hardware settings. Internal search need not be bit-identical if the selected profile is deliberately full-strength/multithread/time-based.

Always state whether the evaluated positions were already covered by an admitted Stockfish calculation generation or were held out/that provider was disabled.

## Cute Chess experiment law

A frozen match generation binds at least:

```text
Stockfish opponent generation
Stockfish analysis provider + inclusion/holdout law
cutechess version/orchestration
hardware / affinity / serviceable-or-isolated resource profile
paired/color-swapped opening suite
clock/depth/nodes/adjudication law
Laplace evidence/world epoch
Laplace firmware/operation recipe
```

The harness owns process lifecycle, paired openings/colors, timing/adjudication and PGN/result artifacts. New PGNs may be admitted **after** the frozen experiment closes into a later evidence epoch; the benchmark must not train/admit its own match results into the state it is simultaneously measuring unless that adaptive experiment is explicitly the subject.

## Cumulative/ablation proof ladder

A versioned experiment may add planes cumulatively, for example:

```text
A0 legal/tactical proposal + exact material baseline
A1 + PST / phase
A2 + deterministic structure planes
A3 + motifs / geometry / tablebase where selected
A4 + optional Stockfish classical-analysis plane
A5 + learned/derived structural residuals
A6 + global PGN trajectory/outcome evidence
A7 + player/opponent/rating/time conditioning
A8 + openings/books/catalog providers
A9 + lexical/sense/cross-modal evidence
A10 complete selected Chess Forward Pass
```

The sequence is an experiment recipe, not permanent ontology.

For each rung where feasible, compare:

- `Ai` vs `Ai-1` under matched resources;
- `Ai` vs one frozen external Stockfish reference;
- full vs full-minus-one selected plane;
- source/provider ablations using identical challenge/opening/resource boundaries.

Report W-D-L, Elo/uncertainty or SPRT where appropriate, CPU/nodes/memory/wall, crashes/time losses, exact provider identities and raw PGN/config/result artifacts. Do not promote mid-run score into final evidence.

## Move / analysis receipt

A selected move or analysis can preserve separate contribution state such as:

```text
exact legal/material/tactical state
deterministic structure calculations
Stockfish calculation when selected
PGN/player/trajectory observations
book/expert testimony
lexical/sense state
opening/tablebase/motif/geometry routes
standing / contradiction / uncertainty / dependence
hops / fanout / candidate/search work
provider/operator identities
final obligation/selection reason
realization + witnessed consequence
```

Observed/expert evidence traces to provenance/dependence roots. Calculations trace to exact recipes/provider generations.

## Resource admission

Chess inherits the same compute-envelope product law. Deeper analysis is more hops/fanout/search/provider/native work over the same entitled knowledge world, not a different deliberately knowledge-reduced chess model.

Before expensive analysis, the plan can estimate/reserve its search/candidate/provider/resource ceiling and reconcile against the actual receipt afterward.

On the managed 6C/12T host, benchmark/search experiments claiming **serviceable** throughput must preserve database/product/runner/control-plane headroom. Full CPU/SMT saturation is a separately labeled isolated experiment.

## Acceptance

The proving domain should demonstrate:

- equal canonical chess states converge across PGN/book/self-play/calculation sources under the same recipe;
- game/event occurrences remain distinct while reusable calculation results do not line-amplify;
- exact trajectory/order/provenance survives transpositions/repeated structures;
- Stockfish generation changes invalidate incompatible calculation-cache namespaces;
- deterministic calculation reruns reproduce or raise a health discrepancy;
- whole-state COUPLE changes routing/selection when relevant chess/lexical/book/player/provider evidence changes;
- disabling one provider/channel produces a receipted ablation rather than private code path;
- Stockfish cannot silently decide Laplace's final move in a general forward operation;
- each move updates the next coupling/frontier;
- scalar/reference and accelerated/native paths preserve semantic parity;
- search hot loops remain coarse native operations rather than per-node managed/SQL boundaries;
- fixed-ruler match results are reproducible from exact code/provider/world/challenge/resource receipts;
- observed games and new match results do not self-certify as truth merely because Laplace produced them.

## Scientific outcome

The experiment is allowed to show either result.

If a completed Laplace chess program eventually beats a pinned full-strength Stockfish generation under a statistically defensible fixed protocol, that is measured evidence. If it does not, the cumulative/ablation receipts identify which expected sources/algorithms fail to add strength or cost too much.

Chess is useful precisely because it makes the common Laplace architecture falsifiable under exact rules and an unusually strong external reference.
