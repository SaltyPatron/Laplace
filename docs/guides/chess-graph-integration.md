# Chess graph integration — PGNs join the same world, not a private chess island

Chess is useful in Laplace because every chess source decomposes into the same canonical entity/relation/physicality substrate used by the rest of the system. A PGN is not one opaque "chess record" that feeds a chess-only engine. Its players, line, playing occurrence, event, result, positions, moves, ratings, clocks, openings, motifs, external profiles, book explanations, and calculated metrics all become separately addressable state that can converge with state admitted from other sources.

The governing law remains:

> same canonical content = same BLAKE3 identity; occurrence, source, context, testimony, calculation, and realization remain separate.

## The missing integration picture

A representative PGN path is:

```text
PGN artifact / source record
│
├─ White/Black names
│    │
│    └─► Chess_Player
│          ├─ HAS_NAME_ALIAS ─► "Carlsen, Magnus"
│          ├─ HAS_NAME_ALIAS ─► "Magnus Carlsen"
│          ├─ HAS_RATING ─────► rating observations
│          ├─ PLAYED_BY ──────► opponent Chess_Player
│          ├─ CORRESPONDS_TO ─► explicitly associated provider identity
│          └─ external/profile ─► provider id, FIDE id, title, federation, biography, links
│
├─ named tournament/site/date
│    └─► Chess_Event
│
├─ one played source occurrence
│    └─► Chess_Playing
│
├─ ordered board-content line
│    └─► Chess_Game / LINE
│          │
│          └─ physicality trajectory
│               P0 ─M0→ P1 ─M1→ P2 ─M2→ ... ─Mn→ Pn+1
│
├─ witnessed record joins
│    ├─ PLAYS_LINE
│    ├─ HAS_WHITE / HAS_BLACK
│    ├─ HAS_EVENT
│    ├─ HAS_RESULT
│    ├─ ON_DATE
│    ├─ HAS_TIME_CONTROL / HAS_TC_CLASS
│    └─ other admitted PGN metadata
│
└─ calculated/derived joins over the same canonical line/state
     ├─ opening / ECO
     ├─ motifs / shape / structural calculations
     ├─ material / PeSTO / rook-file / pawn-structure state
     ├─ Stockfish classical analysis generation
     └─ Syzygy exact closing/catalog state where applicable
```

The graph is therefore not `PGN -> engine score`. It is a dense set of reusable joins into one entity world.

## Player identity and name realization

Current old-Laplace player identity uses `ChessVocabulary.PlayerId(name)` over `PlayerAlias.Canonical(name)`.

The canonicalizer deliberately makes forms such as:

```text
"Carlsen, Magnus"
"Magnus Carlsen"
```

resolve to the same chess player identity. Display/name strings are still emitted as `HAS_NAME_ALIAS` content, so identity and realization are not the same state.

This is why a graph can show a human-facing `Carlsen, Magnus` or `Magnus Carlsen` while traversing the same canonical player node.

Online provider handles are a different case. A provider identity such as `MagnusCarlsen` can be retained as the provider's own player/profile identity and carry aliases/display/real-name evidence. The profile ingest code does **not** declare identity merely because two strings look similar. When an online profile and exactly one FIDE profile are explicitly supplied together, the online provider identity can receive a `CORRESPONDS_TO` edge to the FIDE-side player identity.

That distinction is intentional:

```text
string/name similarity      != identity proof
explicit provider linkage   -> governed CORRESPONDS_TO testimony
```

A FIDE profile also deposits an external-id value such as:

```text
fide:<provider-id>
```

plus title/federation/rating/profile facts on the same player/profile world.

## Games connect players to each other

Chess does not need a separate opponent table to make Magnus and Hikaru neighbors.

The ingest/calculation layer already folds head-to-head state as:

```text
(player, PLAYED_BY, opponent)
```

with the playing/game retained as evidence context. Repeated meetings therefore converge on one player-to-opponent cell while preserving individual game provenance.

A player entity world can consequently expand through:

```text
Magnus
  ├─ PLAYED_BY -> Hikaru
  ├─ PLAYED_BY -> Firouzja
  ├─ ...
  ├─ games/LINEs in which Magnus was White
  ├─ games/LINEs in which Magnus was Black
  ├─ openings/ECOs reached in those lines
  ├─ positions and moves contained in those trajectories
  └─ ratings/results/time/source/profile state attached around them
```

That is the "web of a world champion" behavior visible in the old Explore UI.

## Moves and positions connect back to players through playing context

A move does not need to carry a permanently flattened `MOVE -> Magnus` edge for the player-conditioned graph/query to exist.

Current player-move reads join move evidence through the playing context to `HAS_WHITE` / `HAS_BLACK`. Conceptually:

```text
canonical position P
  └─ MOVE / transition M
       └─ evidence context = playing G
            ├─ HAS_WHITE -> Magnus
            ├─ HAS_BLACK -> opponent
            ├─ HAS_RESULT -> result
            ├─ ON_DATE / time control / rating context
            └─ PLAYS_LINE -> shared LINE
```

So the same canonical move/position can be observed in games by Magnus, Hikaru, Fischer, the user, Stockfish/cutechess matches, or anyone else without reminting the move.

This is what makes queries such as "what did Magnus play here?", "who reached this position?", and "how did players in this rating band perform after this move?" different projections over the same canonical chess content.

## The LINE is shared game content; the PLAYING is the occurrence

The current vocabulary distinguishes:

- `Chess_Game` / LINE — content-addressed from the ordered position ids; one entity per distinct played line regardless of who played it or when;
- `Chess_Playing` — one source-record occurrence of a line, carrying player/date/event/result provenance;
- `Chess_Event` — tournament/named event shared by many playings.

That separation matters for graph traversal:

```text
Magnus -> playing A -> shared LINE L <- playing B <- Hikaru
```

or:

```text
Magnus -> playing A -> LINE L -> position P -> move M
                                      ^
                                      |
                         another book/game/source
```

Same line/state/move can therefore become a bridge across players, events, eras, sources, and modalities.

## Grandmaster books join the same line/state world

A grounded book variation is not a private "book vector". The chess book lane is intended to resolve replayed book analysis onto the same `ChessCompose.LineId` / canonical positions used by PGNs.

That means:

```text
Grandmaster book paragraph
  ├─ ordinary text physicality
  ├─ words/senses/definitions from the language substrate
  ├─ attributed EXPLAINS / recommendation / criticism testimony where extracted
  └─ grounded chess variation
        └─ same LINE / positions / moves as recorded games
```

Two different books teaching the same line can therefore converge on the same chess content while remaining distinct book/source witnesses.

This is also where WordNet/Wiktionary/OMW terms such as `fork` and `gambit` stop being a separate NLP feature. The book occurrence, lexical sense, calculated board motif, PGN occurrence, player history, and Stockfish evaluation can all meet around the same canonical entities under one query/firmware program.

## Stockfish and other classical metrics attach to the same graph

Stockfish analysis is another calculated plane over canonical chess state. It should attach to the reusable position/move/transition coordinate, not create one evaluator-specific copy per PGN.

The intended join is:

```text
PGN A ---------\
PGN B ----------\
book ------------> canonical position P -> move M -> position Q
self-play -------/          |                          |
                           ├─ material / PST / structure
                           ├─ Stockfish generation
                           └─ Syzygy when applicable
```

Real game recurrence increases observed game evidence. Re-running one deterministic Stockfish generation must not become extra independent support merely because many games contain P. Old issue #1430 owns the measured current defect where engine work is memoized by position but semantic deposition can still be amplified per LINE.

## What the generic Explore graph should materialize

An entity-world graph is a bounded materialization over this shared state, not a chess-private topology.

Starting at a player such as Magnus may legitimately surface, according to selected provider/relation/hop rules:

```text
player identities
aliases / names / handles
provider/FIDE mappings
opponents
playings
events
results
ratings
shared LINEs
positions
moves
openings / ECO
motifs
book explanations
calculated metrics
sources / provenance
```

From any of those nodes, recentering should continue through the normal generic entity-world contract.

For example:

```text
Magnus
 -> PLAYED_BY -> Hikaru
 -> one shared playing/LINE
 -> position P
 -> move M
 -> another game containing M/P
 -> another player
 -> that player's profile/FIDE/alias world
```

This is why the old screenshot is a web rather than a player profile with decorative edges.

## Acceptance

- `Carlsen, Magnus` and `Magnus Carlsen` resolve to the same intended canonical chess-player identity under the governed alias canonicalizer.
- Provider handle/profile identity such as `MagnusCarlsen` is not merged solely by fuzzy/name similarity; explicit provider association is receipted through `CORRESPONDS_TO` or the selected identity mechanism.
- A FIDE/provider id remains external/profile state and does not salt the canonical human/player content merely because a source carries it.
- PGN `HAS_WHITE/HAS_BLACK`, `PLAYED_BY`, result/event/rating/time state remain traversable from the player world.
- A player-conditioned move query reaches the player through move/playing context rather than requiring a duplicated player-specific move identity.
- Two players who reach the same canonical position share that position node while retaining independent game occurrences/outcomes.
- A grounded grandmaster-book line and a PGN line converge on the same line/position/move content where they actually describe the same chess sequence.
- Stockfish/classical analysis attaches to shared canonical position/move state and does not multiply one deterministic opinion by PGN recurrence.
- Generic entity-world expansion can traverse player -> game/line -> position/move -> other game/player/profile/source and back without a chess-private graph implementation.
- Human-readable labels are realization; unresolved hashes are canonical ids leaking through an incomplete realization path, not alternate graph identities.

## Related ownership

- #491 — canonical chess position identity versus Zobrist accelerator
- #574 — grandmaster-book admission/grounding
- #833 — complete Chess Forward Pass
- #838 — chess natural grain / trajectory state
- #840 — player-conditioned evidence
- #1398 — player identity contamination defect
- #1401 — universal observation/fact/calculation forward-pass integration
- #1404 — generic entity-world product surface
- #1424 — Stockfish/Cute Chess proving stack
- #1430 — canonical Stockfish calculation dedup
- Laplace-Refactor #68/#132/#136/#139/#164 — clean entity-world, provider, chess, comparator, and calculation contracts
