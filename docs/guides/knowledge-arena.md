# Knowledge Arena — games over the Laplace world

This guide records the product/game family discovered from the existing Laplace entity-world,
Matchup, graph, trajectory, evidence, and chess surfaces. It is deliberately a **consumer of the
Laplace machine**, not a second game-specific intelligence stack.

Active implementation ownership is in GitHub issues, especially #1401, #1404, #1420, and #1421.
The clean implementation counterparts are tracked in `Laplace-Refactor` #17/#18/#60/#68/#132/
#137/#138.

## One machine, several verbs

The product-level operations are useful because they name different obligations over the same
canonical substrate:

```text
MATCH      A vs B      compare/shared/different/current evidence
CONNECT    A -> B      find an admissible typed path
COMBINE    A + B       satisfy a composition/novelty obligation from both worlds
EXPLORE    A -> ?       materialize a bounded entity world
PLAY       program + rules + event trajectory over the same operations
```

None of these owns private identity, graph truth, KNN, embeddings, language meaning, scoring, or
search. They compile into the same ISA/forward machinery that conversation, chess, model
compilation, and other modalities use.

The game layer therefore demonstrates the invention rather than covering it with game-specific
logic.

## Entity worlds are the board

A Laplace entity world is a bounded, query-relative materialization around a canonical entity.
The old Warehouse and chess-player webs are the behavioral proof: a player such as Magnus can be
rendered as a web of games, opponents, outcomes, sources, and neighboring entities, then any node
can become the center of its own world.

A game may pin:

```text
root/start/target entities
world/evidence epoch
relation/provider families
source/context/time/domain/sense scopes
hop/search boundary
fanout/frontier/resource budget
standing/evidence threshold
visibility policy
firmware/rules/scoring
```

The rendered graph is a view over that selected world, not a stored universal adjacency and not
semantic authority.

### Identity is not the label

Raw BLAKE3/content-addressed ids remain the canonical node identity. Human names, handles,
notations, and translations are realization state. A node rendered as a shortened hash means the
selected realization is unresolved; the node is not missing and must not be reminted to make the
UI pretty.

Human-readable and raw-id/debug modes may render the same game board differently while preserving
byte-identical node/edge identity.

## Ranked fairness: the Tetris property

A ranked match should give every contestant the equivalent of the **same Tetris piece sequence**.
It pins one challenge generation:

```text
challenge_set_id + ordered queue
closed world/evidence epoch
provider/relation/calculation rules
sense/domain/source/time scopes
semantic radius/path boundary
anti-hub/specificity law
visibility policy
resource limits
scoring/tie law
server timing/event ordering
```

Two contestants can then be compared mechanically because the semantic world and challenge order
are the same. A later ingest produces a new challenge generation rather than silently changing the
old course.

Practice mode may intentionally use the live current installation.

## Games are physicality trajectories

A completed game is naturally another ordered event:

```text
challenge
  -> selected entity/state
  -> selected relation/action
  -> selected entity/state
  -> ...
  -> completion / failure / timeout
```

The canonical content is shared globally; each occurrence in the player's event retains its own
ordinal/physical position and event context. There is no need to manufacture permanent
`PRECEDES` testimony for every move through the game: the ordering is already present in the
physicality trajectory.

### No repeats in the same event

Many traversal games can select a self-avoiding rule:

```text
next entity/state must not already occur in this event trajectory
```

This prevents cycle farming and makes each step consume navigation options. It is **firmware**, not
a universal substrate law. Narrative, temporal, or resource puzzles may legitimately revisit the
same canonical state under different event/time/resource context.

## Knowledge Golf

Knowledge Golf is the cleanest flagship game.

```text
start entity  = tee
target entity = hole
transition    = stroke
best admitted path under the rules = par
```

A course can constrain relation families, minimum evidence, domain crossings, historical cutoff,
maximum radius, source types, or generic-hub use.

Example:

```text
Magnus Carlsen -> Apollo 11
Par: 6
No universal taxonomy hubs
Must cross >= 3 domains
At least one edge >= 100 witnesses
At least one admitted book/document witness
```

Useful modes include:

- **Daily 9** — nine immutable holes for one pinned generation;
- **Speed Golf** — validity/strokes first, time as a tie breaker or combined declared score;
- **Evidence Golf** — optimize typed path cost/evidence quality instead of raw hops;
- **Historic Golf** — only evidence valid/admitted before a time boundary;
- **Blind Golf** — no global graph preview;
- **Tier Golf** — required structural-altitude changes;
- **Multimodal Golf** — required domain/modality crossings;
- **Masters** — heavily constrained tournament courses.

A direct admissible relation is literally a **hole in one**.

## Highway Race / Knowledge Sprint

This is the strongest competitive-Tetris analogy.

Every contestant receives the same ordered queue:

```text
1. gecko -> telephone
2. Mozart -> Saturn
3. Magnus -> Alan Turing
4. fork -> Linux
5. sodium -> Napoleon
...
```

Each player advances to the next challenge immediately after completing the current one. They do
not wait for the opponent. Server time, legal transitions, path cost, and penalties come from the
same challenge/trajectory receipt.

This rewards throughput and navigation skill rather than stochastic model output.

## Laplace Degree — Bacon/Erdos generalized

Laplace can generalize Bacon/Erdos-style degrees to any two addressable entities, but there is no
single context-free universal distance.

A degree card must name its rule and epoch, for example:

```text
raw degree                 minimum admissible transitions
witnessed degree           every edge passes an evidence floor
typed degree               selected relation families only
temporal degree            world restricted to a historical boundary
source/domain degree       restricted provider/source families
cross-domain degree        path must cross selected domains/modalities
```

The scalar is always accompanied by the path/rule/epoch receipt.

Entity pages can expose connection fingerprints against familiar anchors:

```text
"gambit"
  Magnus       2
  Erdos        5
  Shakespeare  3
  Linux        6
  Apollo 11    5
```

Those are product facts only under the displayed degree law, not a universal importance metric.

## Relay family

Relays use the same trajectory engine with different visibility/transition obligations.

### Blind Relay

Show only the current entity and the permitted local exits. The target of round N can become the
start of round N+1.

### Distance Relay

Each leg has a declared maximum semantic radius/path depth.

### Tier Relay

Require altitude transitions, for example:

```text
word -> sentence -> document -> person -> organization
```

or the reverse.

### Source Relay

Require source/provider changes on successive steps, such as lexical -> book -> PGN -> encyclopedia
-> repository.

### Modality Relay

Require materially different domain/modality state, such as word -> image -> location -> audio ->
person -> chess game.

### Relation Relay / Stack

Give every contestant the same deterministic relation sequence, much like falling Tetris pieces:

```text
IS_A
PART_OF
USED_BY
AUTHORED_BY
LOCATED_IN
...
```

Every move must satisfy the next relation obligation. A locally valid choice can make the next
piece impossible, turning the game into planning rather than raw speed.

## Collision / Capture the Flag

Two players or teams start in different parts of one bounded entity world and navigate toward each
other, a shared objective, or an opposing flag/base.

Possible firmware includes:

- first valid path intersection wins;
- lowest combined path cost wins;
- capture enemy flag then return to home through a legal path;
- visited nodes become unavailable to that player;
- team-visible explored state / fog of war;
- controlled nodes become relay/spawn points;
- different player roles expose different provider families.

The outbound shortest route can be a poor capture-and-return route when no-repeat rules consume the
bridge required to escape.

## Choose Your Own Adventure / world games

The substrate can be the world while firmware defines what kind of adventure is being played.
Prior choices remain part of the active event trajectory and can constrain later actions:

```text
visited entities/places
objects acquired
claims learned
people encountered
actions performed
time/resource state
relationships formed
```

Possible genres include historical adventure, science exploration, literary worlds, code/cyber
worlds, roguelikes, mysteries, and D&D-like sessions. Revisit behavior is genre/program specific.

A save is principally the pinned world/program identity plus the event trajectory and any declared
session/effect state.

## Constraint Crossing / Constraint Gauntlet

The wolf/sheep/hay ferry, bridge-and-torch, missionaries/cannibals, scheduling, routing,
inventory, sliding, and similar puzzles are state-transition programs over the same machine.

Example:

```text
state = { wolf:left, sheep:left, hay:left, boat:left }
operation = move boat with <= 1 passenger
constraints:
  wolf + sheep unsupervised => invalid
  sheep + hay unsupervised  => invalid
goal = all:right
```

Each legal state is a canonical composition; each action is a transition; the solution is an
ordered state/physicality trajectory. The puzzle tests guidance/search/effect/witness behavior,
not a puzzle-only solver.

## Graphle / Hidden Entity

Graphle must be sequence-aware. A 4D centroid alone is insufficient.

`act`, `cat`, and `tac` are the canonical counterexample: the same constituent multiset can share a
centroid while ordered physicality trajectories differ.

A Graphle hint may independently reveal:

- centroid proximity;
- Frechet/trajectory distance;
- ordinal/gap similarity;
- constituent/set overlap;
- tier/altitude;
- typed relation degree;
- containment/domain/sense overlap.

The UI should say which channel a hint represents rather than collapsing everything into one opaque
"distance". A centroid-only implementation must fail the order-sensitive fixture.

This enables a useful subgame: **Same Place, Different Path**.

## Witness Hunt — installation-relative by design

Witness Hunt should not be discarded because answers change as data changes. The installation/data
boundary is part of the query.

### Live Hunt

Intentionally query the current admitted world:

- earliest/latest witnessed occurrence currently present;
- strongest independent support or refutation;
- first source connecting A and B;
- exact book/game/document containing a displayed claim;
- strongest provenance chain currently admitted.

Dynamic results are the point.

### Ranked/Pinned Hunt

Bind exact installation/data manifest + closed evidence epoch + source/time/world scope. Publish
only challenges whose answerability/completeness obligations are satisfied for that boundary.
Replay remains deterministic after the live installation advances.

### Forensic/Operator Hunt

Turn the same mechanic into substrate/data-quality work:

- find the witness that changed a result between epochs;
- find duplicated dependence masquerading as independent support;
- locate a contradiction;
- locate a missing expected source;
- find the exact evidence responsible for a surprising standing;
- diagnose a deliberately damaged fixture.

Different Laplace installations can therefore be different courses rather than defective copies of
one supposedly universal trivia database.

## Red / Blue / White adversarial orchestration

The serious form of Contradiction Duel is the intended cybersecurity firmware split, not an LLM
argument.

- **Red** executes authorized offensive/discovery/evasion programs inside a declared environment.
- **Blue** detects, prevents, contains, attributes, and repairs.
- **White** owns environment authority, rules, effect boundaries, telemetry, timing, resets, and
  adjudication.

Red/Blue statements remain attributable testimony. White-observed process/network/filesystem/tool/
effect state is independent physicality/effect evidence.

```text
Red: "I escaped."
White trajectory:
  process -> syscall -> socket attempt -> policy denial
```

The self-report is not the effect. Conversely, if the authoritative effect trajectory proves a
boundary was actually crossed, White records that rather than trusting Blue's "blocked" claim.

Scoring can include effect achieved/prevented, detection latency, containment, persistence, false
positives, resource use, and evidence quality. Ranked cyber games must remain in explicitly
authorized/sandboxed challenge environments.

## Chess-map hybrids

Chess can use the same game machinery without replacing ordinary chess rules.

One mode can make the board transition deterministic while the player must navigate the admitted
chess world to justify/discover a candidate move:

```text
current position
  -> structure/motif/opening/player/book/game world
  -> prior trajectory/analogue
  -> candidate move
  -> legal chess transition
```

A King's Road / Capture-the-King mode can treat the current king/home as a base and historical
positions, games, motifs, books, and players as terrain. Stockfish/tablebase/classical calculation
still acts only under its declared provider role; the route itself is a Laplace knowledge
trajectory.

This is a visualization/game form of the same Chess Forward Pass, not a separate chess engine.

## COMBINE / Craft

COMBINE is distinct from Matchup. It searches both entity worlds for admissible bridges,
transformations, analogies, or compositions, then realizes a result with derivation provenance.

`gecko + telephone` is the proving fixture. The answer is not hardcoded; a finite world can honestly
produce no admissible result at one radius and a valid result at a larger radius.

Semantic radius and physical cost remain separate:

```text
smaller allowed radius -> semantically harder to connect
larger allowed radius  -> easier to find some connection
larger search frontier -> generally more computation
```

Raw shortest path is not automatically the best semantic bridge. Universal hubs such as `entity`
or `object` can be rejected/dominated by a specificity-requiring firmware.

Generated compositions retain generation ancestry and do not self-certify as observed truth.

## Post-round facts and spectator value

Because every route is receipted, the product can derive useful facts without inventing a global
importance score:

- par / best admitted path under identical rules;
- player's excess path cost or strokes;
- rarest bridge used in challenge history;
- strongest/weakest-supported edge on the route;
- oldest/newest witness traversed;
- source/domain/language boundaries crossed;
- most disputed edge;
- equal-cost alternate routes;
- route valid in one historical epoch but not another;
- first challenge-history use of a route/bridge;
- average semantic radius, search work, and decision time;
- "N degrees from X" cards under a named degree law.

Challenge-history novelty is social/game state, not evidence that a relation is globally novel.

## Scoring

No universal score is required. A challenge recipe can compare components lexicographically or by
declared weights, but the receipt preserves the raw components:

```text
valid completion / obligations satisfied
server elapsed time
transition count / typed path cost
specificity/evidence compliance
physical resource work
penalties
optional declared novelty/diversity
```

Validity and hard constraints precede speed/style. Every ranked score must be recomputable from the
challenge identity plus the event/path receipts.

## Implementation ownership

- #1401 — one forward-pass/operator mechanism across observation, fact, calculation, and standing;
- #1404 — generic entity-world materialization and realization;
- #1420 — COMBINE semantic act;
- #1421 — Knowledge Arena competitive/product family;
- #833/#1419/#1424 — chess cross-modal proof and comparator ladder;
- Refactor #137/#138 — clean COMBINE and Arena consumers.

The implementation criterion is not the number of game modes. The criterion is that materially
different games reuse the same identity, trajectory, evidence, query/search, realization, effect,
and witness machinery.