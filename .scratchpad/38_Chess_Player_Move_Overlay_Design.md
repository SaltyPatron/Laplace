# 38 — Chess board overlay: "who plays what here" (top-roster move attribution)

Design doc, written 2026-08-01. Status: PROPOSED, not implemented. Every code
citation below was verified against the tree on that date; re-verify line
numbers before editing. Counts are as-of and owned by
[docs/INVENTORY.md](../docs/INVENTORY.md) / `docs/guides/chess.md`.

## 1. The feature

A toggleable board overlay that answers, for the position on the board: **which
of the top-N roster players played which move from here, in their recorded
games?** Grouped by move, not by player:

> e4 — Carlsen, Caruana · c4 — Nakamura · d4 — (nobody in roster on record)

- Destination squares are tinted per move-group (one hue per candidate move,
  same visual grammar as the existing suggestion marks); a legend/panel lists
  each move with the players who chose it, their game counts, and net score.
- N defaults to 20, taken from the rated roster — NOT hand-curated. "Top"
  already means Glicko-2 strength, which prices opponent difficulty (see §3).
- The overlay is a **provenance read over witnessed games** — who actually
  played what — never a fold read. This is a feature, not a limitation: it
  sidesteps the known fold-poisoning gap (guide §known-gaps, #447/#449, where
  self-play testimony dominates raw μ from the start position).
- In the invention catalog's terms this is not a new mechanism: it is the
  provenance half of invention 11 (provenance vs aggregating edge duality)
  read per-player — a context-scoped pour (invention 30's filter-by-context
  move at read grain) — with witness counts surfaced per invention 37
  (explainability as columns). The design's job is to ride those mechanisms,
  not invent parallel ones.

## 2. What already exists (all verified)

| Ingredient | Where | State |
|---|---|---|
| Per-player continuations at a position | `extension/laplace_substrate/sql/functions/chess/chess_player_moves.sql.in` | **Exists.** The hard problem — attributing MOVE evidence to the player who held the colour — is solved there via context-equality threading (GH #736 law in its header: join the playing-EVENT `context_id`, never the colour fact's subject, which is the shared LINE). |
| Top-N roster, opponent-difficulty-aware | `chess_ranked.sql.in` | **Exists.** Reads folded `(player, OUTCOME)` Glicko cells; its header states the law: "Glicko-2 weighs WHO you beat, so 68% against grandmasters outranks 68% against beginners." |
| Position id from FEN | `POST /chess/explore` (`EndpointMappings.ChessRead.cs`) | **Exists.** Composes through the modality (positions are composed entities — `canonical_id('<fen>')` finds nothing; guide §identity). Also already SAN-decodes continuations. |
| Board overlay insertion point | `web/src/chess/play/Board.tsx:208-222` | **Exists.** The single SVG layer (currently user arrows, `elbow()` polylines) plus the per-square CSS-custom-prop hue pattern (`--sugg`/`--teval`, `Board.tsx:168-172`). |
| Toggle + persistence pattern | `GameControls.tsx` checkboxes backed by `useState` in `ChessView.tsx`, persisted via `web/src/chess/playPersist.ts` | **Exists**, ad-hoc per toggle. No overlay registry — see §7 non-goals. |
| Player-name resolution | `realize_batch` (aggregate ids, then batch — CLAUDE.md read rules) | **Exists.** |

What does NOT exist: the set-based read (one player at a time today), the
endpoint, the overlay rendering, and — the actual constraint — the corpus (§6).

## 3. "Taking into account opponent difficulty" — resolved, two layers

1. **Roster selection (v1, free):** `chess_ranked(20)` IS the
   opponent-difficulty-aware top-20. Nothing to build.
2. **Weighting witnesses inside a player's move distribution** by the
   opponent's `HAS_RATING` at that event (a per-game time series, read today by
   `chess_player_ratings.sql.in`): deliberately **v2**. It needs an extra
   per-event join, and v1's display (game counts + net score per player per
   move) already communicates sample weight honestly. Ship v1, measure, then
   decide whether weighted counts change any answer enough to matter.

## 4. The read: one new SQL function

Generalize `chess_player_moves` from one player to a set. New `.sql.in` beside
it (extension surface — **no DbUp migration**, per CLAUDE.md; check
`SELECT * FROM api('chess')` first in case a later session already added it):

```sql
CREATE OR REPLACE FUNCTION chess_roster_moves(
        p_position bytea, p_players bytea[], p_as_white boolean,
        p_limit integer DEFAULT 64)
    RETURNS TABLE(next_position bytea, player_id bytea,
                  games bigint, score double precision)
    LANGUAGE sql STABLE PARALLEL SAFE AS $$
    -- Set-based generalization of chess_player_moves; the GH #736 context law
    -- carries over verbatim: thread CONTEXT equality (events where the player
    -- held the colour), never the colour fact's subject (the shared LINE).
    SELECT a.object_id, g.object_id,
           sum(a.observation_count)::bigint,
           (sum(a.outcome::double precision * a.observation_count)
                / (2.0 * sum(a.observation_count)))::double precision
    FROM @extschema@.attestations a
    JOIN @extschema@.attestations g
      ON g.context_id = a.context_id
     AND g.type_id = @extschema@.relation_type_id(
             CASE WHEN p_as_white THEN 'HAS_WHITE' ELSE 'HAS_BLACK' END)
     AND g.object_id = ANY(p_players)
    WHERE a.subject_id = p_position
      AND a.type_id = @extschema@.relation_type_id('MOVE')
      AND a.context_id IS NOT NULL
    GROUP BY a.object_id, g.object_id
    ORDER BY sum(a.observation_count) DESC
    LIMIT p_limit
$$;
```

Sketch, not final — implementer notes:

- **Colour law is the caller's**, same as `chess_player_moves`: side to move of
  the position decides `p_as_white`. Moves out of a white-to-move position in a
  player's games are theirs only when they were White.
- **Ranking law:** ordered by observation count (evidence-produced), grouped by
  move on the client. The `LIMIT` bounds rows-per-position, not an arbitrary
  cut — `p_limit 64` comfortably exceeds 20 players × the realistic move
  fan-out at any position that has roster coverage.
- **Unseeded/absence law (CLAUDE.md reads):** an unattested (player, move) is
  UNKNOWN, not attested-never-played. The empty result must render as "no
  recorded games from this position among the roster," never as a bare blank
  board, and nothing in the chain may use `EXISTS` to collapse
  unattested-vs-attested-false. Test the unseeded case explicitly
  (`pg_regress` tests the installed extension, not the edited `.sql.in`).
- **Plan check before trusting it:** `EXPLAIN` with **bound parameters**. The
  join fan-out is (MOVE rows at position) × (colour-fact rows matching 20
  players by context). The start position is the worst case by construction —
  it appears in every standard game, and cost scales with degree (CLAUDE.md:
  re-time after every seed; a rare-position timing tells you nothing). If the
  context join doesn't stay index-driven at post-corpus scale, this becomes a
  C-side candidate (per-row set-returning work belongs in C, not a rewritten
  CTE) — but measure first.
- Keep the body free of `SET`/`STRICT` if any `eff_mu`-style inlining path ever
  touches it.
- **Storage-law dependency (#451 — read §6 first).** The sketch above reads
  per-occurrence `MOVE` evidence rows joined on `context_id` — the exact row
  class #451 rules derivable/virtual. This query is not new law: it is spec
  11's own isolation query ("Magnus's E2E4 only: attestations for E2E4 whose
  context game has the player; re-fold player-scoped"), and #451 explicitly
  preserves it — scoped pours/refold via vertex filtering, per-occurrence
  provenance derived from the game trajectories the context already owns.
  The FUNCTION CONTRACT here (position, players[], colour → move, player,
  games, score) survives either law; only the body changes. The implementing
  session must check which law is in force and, if #451 has landed, write
  the body as a trajectory-derived read (roster games are bounded, and each
  game is ONE trajectory row) instead of an evidence-row join. Do not build
  new read surface that assumes materialized per-ply evidence without
  checking #451's status first.

## 5. Endpoint and frontend

**Endpoint** — new read in `EndpointMappings.ChessRead.cs`:

```
POST /chess/overlay/players   { fen, players?: int = 20, limit?: int = 64 }
```

Server: compose position id from FEN (as `/chess/explore` does) → resolve side
to move → `chess_ranked(players)` for the roster ids → `chess_roster_moves` →
SAN/UCI-decode the `next_position` ids (reuse explore's decode) → aggregate
player ids and resolve names through `realize_batch` (never per row). Response:

```json
{ "sideToMove": "w",
  "roster": [ { "id": "...", "name": "Magnus Carlsen", "rank": 1 } ],
  "moves": [ { "uci": "e2e4", "san": "e4",
               "players": [ { "id": "...", "games": 41, "score": 0.62 } ] } ],
  "coverage": { "rosterGames": 120, "positionsKnown": true } }
```

`coverage` exists so the UI can state absence honestly (§4). Response shapes go
in `web/src/chess/db/types.ts`, client call in `web/src/chess/db/api.ts`.

**Dense-coverage consequence (corpus in hand, §6):** once roster games number
in the hundreds of thousands, every roster player appears on every mainline
move and raw presence carries no signal. The stat that matters becomes each
player's **choice share**: games with this move ÷ that player's games reaching
this position ("Carlsen: 61% e4 here; Nakamura: 12%"). Shares must be computed
against the player's TRUE per-position total — if the read truncates
(`p_limit`), compute totals server-side (or return a per-player
`positionGames` alongside the move rows), never by summing the truncated
client rows. The e4-vs-x contrast in §1 is really a share contrast at scale.

**Frontend** — smallest honest version:

- New optional prop on `BoardProps` (`Board.tsx:48`):
  `playerMoves?: { uci: string; hue: number; players: {name, games, score}[] }[]`.
- Render in the existing SVG overlay layer: one `elbow()` polyline per
  move-group (distinct hue per move, the `suggMark` hue-ramp pattern at
  `Board.tsx:97-104` is the precedent) + destination-square tint via the
  established CSS-custom-prop route. Player attribution (names, counts, score)
  goes in a legend panel below the board, NOT on-square — 20 names don't fit
  in a square, and the existing legend row (`Board.tsx:225-231`) is the slot.
- Toggle: one `Checkbox` in `GameControls.tsx` ("roster moves"), state in
  `ChessView.tsx`, persisted in `playPersist.ts` — the established pattern.
  Also wire into the DB replay surface (`web/src/chess/db/GameBoard.tsx`, which
  reuses `Board` with `readOnly`): replay is where "what would the top players
  have done here" is most natural.
- Fetch on position change only while the toggle is on; debounce scrubbing in
  `GameBoard` (the replay scrub can cross 10 positions/second).

## 6. The actual constraint: write-path scale (corpus is IN HAND)

Corpus availability, recorded 2026-08-01 from the operator: **the full OTB
record since 1900 plus online games since 1995 — millions of games — is
already held locally.** Acquisition is solved. That inverts the constraint:
the substrate held ~9.5k games as of 2026-07-23, and the question is no
longer where to get games but how much of the corpus the write path can
afford to *analyze*. (Live-box note, 2026-08-01: `source_counts_approx()`
shows 11 lexical sources and ZERO chess sources — the box is mid-reseed.
Any local measurement below needs a chess seed first.)

**The dedup law first, because every cost number depends on it.** A million
Scholar's Mate games are NOT a million game rows. The mechanism chain
(inventions 1/9/21/23, docs/INVENTIONS.md; all code-verified 2026-08-01):

- The **line id is composed from the ordered position-id sequence**
  (`lineId = ChessCompose.LineId(positionIds)`, `ChessPgnDecomposer.cs:183`),
  and positions are themselves content-composed — so a million identical
  playings are **ONE line entity**, and `IsProvenPresent` trunk-skip means
  playings 2..1M never even re-walk the content tree (rule #8: client-side
  dedup, COPY of proven-novel rows only).
- Each playing adds one **EVENT entity** (provenance: who/when/where) and
  per-playing evidence subjecting the LINE with `ctx = event` — the #736 law
  verbatim (`EmitGame`, `ChessPgnDecomposer.cs:440-443`): "evidence stays
  per-playing while consensus cells aggregate across playings."
- The fold cell `(line, OUTCOME, result)` accrues one witness per playing —
  "**witness_count IS 'times played'**" (`ChessPgnDecomposer.cs:459-461`).
  One cell, a million witnesses. Line-grain facts with `ctx = null`
  (`GAME_HAS_ECO/OPENING`, lines 275-277) merge at the EVIDENCE grain too —
  one row whose observation_count accumulates.
- Movetext is a merkle over **shared SAN tokens** — "there are only a few
  thousand distinct SAN tokens in all of chess, so 'Nf6' is ONE entity
  witnessed across millions of games" (lines 296-300). Identically-annotated
  movetext dedups whole; differently-clocked playings are distinct movetext
  documents on ONE line, distinguished by context (lines 335-337).
- The game trajectory subjects the **LINE**, marker-gated per line
  (`ChessTrajectoryDecomposer.cs:56-57,128`) — one trajectory per unique
  line, NOT per game.
- Analyzer deposits fold onto deduped move/position entities: "a move played
  in 10M games is stored once with 10M witnesses" (invention 21); a
  depth-grounded answer is O(1) at query because depth was consumed at fold
  time (invention 23).

So corpus cost has three different drivers, and they scale differently:
**content** (unique positions/lines/tokens — deduped by hash, sublinear in
games, heavily so through opening theory and duplicated games), **provenance
entities** (one event per playing — irreducible, game-grain, cheap), and
**witnessing** (per-playing evidence rows today; the #451 target). The
corpus's redundancy is not a cost to route around — agreement landing on one
cell IS the mesh.

**The cost model is the current storage law, not physics — and that law is
already ruled wrong at this scale.** The analyzer lane today materializes one
evidence row per ply per game (`MOVE` + `OUTCOME`; 11.2M `OUTCOME` rows at
9.5k games, 14.3% of `consensus_rdefault`, the largest writer —
`relation_types.toml` chess block comment). Extrapolated naively that is
billions of rows for a ~10M-game corpus. But **#451 (witness-trajectory
evidence virtualization, operator-prompted design 2026-07-15, B-series
schema law, sign-off pending) names exactly these per-occurrence rows as
"the billion-row pressure" and rules them derivable**: facts dedup at the
consensus key, testimony packs into the existing 32-byte vertex class
(O(witnesses) vertices on O(facts) rows), and per-occurrence provenance
needs no rows at all because **the game IS its ply list** — the context's
own trajectory holds it, and `ChessTrajectoryDecomposer` already writes one
ordered position linestring per game. The recorder lane already made this
exact move for the same reason (`ChessPgnDecomposer.cs:238-244`: per-ply
record rows removed after ~40M of 62M consensus rows proved permanently
single-witness). The analyzer's per-ply materialization is the last
pre-#451 shape standing.

**Therefore the full-corpus path IS #451** (plus its doc-08 amendment,
#535), not a bigger disk. Under the virtualized law, ~10M playings cost:
one event entity per playing, content rows only for UNIQUE lines/positions/
tokens (the dedup law above — trajectories are per unique line, not per
game), and witnessing as packed testimony vertices instead of per-occurrence
rows — a feasible regime, with #588 still governing raw write throughput of
whatever is written. Plan the corpus ingest as **#451-gated**, not as a
per-ply-row campaign:

1. **Do not run a full-corpus analyze under the current law.** Billions of
   materialized per-ply rows would be written into a storage shape the
   substrate has already decided to replace — write amplification with a
   planned demolition.
2. **The recorder lane is already dedup-correct** (game-grain: unique
   content + one event per playing + per-playing evidence; its per-ply rows
   were removed for exactly the single-witness reason —
   `ChessPgnDecomposer.cs:238-244`). Its remaining per-playing evidence rows
   (~15 header facts × games) are the one recorder cost #451 would compress;
   content cost is bounded by unique lines, not games. Recording the full
   corpus is therefore NOT scale-blocked the way analysis is; it is its own
   decision on disk/throughput (#588) and identity readiness (below), and it
   is the natural first pass: record once, derive under whichever analyzer
   law is in force when derivation runs (all lanes are marker-gated; a later
   analyzer version re-derives).
3. **Interim only if the overlay must ship before #451 lands:** a
   roster-scoped analyze (candidate filter — top ~100 all-time by
   header Elo/title — at the CLI or as a scoped analyze command; new work
   either way, §8) bounds the pre-#451 write to ~300k–1M games, 30–100×
   today. Name it as throwaway pressure and decide whether shipping early
   is worth writing rows the #451 migration will re-shape.

**Prerequisites that survive the inversion:**

- **Identity, now ×126 years of name variants.** `PlayerAlias.Canonical`
  folds "Carlsen, Magnus" ≡ "Magnus Carlsen", but the guide's caveat stands —
  chess.com handles canonicalize as themselves ("magnuscarlsen" ≠ "magnus
  carlsen") — and a century of OTB sources adds transliteration drift
  (Kasparov/Kasparow/Каспаров). Stress-pass the alias law against the actual
  candidate list **before** bulk ingest, or players split into multiple
  roster rows; `CORRESPONDS_TO` bridges exist for repair but prevention is
  cheaper.
- **Ops law at multi-day scale**: one ingest at a time; a push to `main`
  restarts postgres and kills a running ingest (`gh run list` first — and
  plan pushes around a week-long run, or accept marker-resume churn);
  redirect output to a log. All lanes are marker-gated/idempotent, so a
  killed run resumes — that is the safety net, not the plan.
- **Re-time after the seed.** Every read this doc touches, especially
  `chess_roster_moves` at the start position, whose degree grows with every
  game ingested (CLAUDE.md: cost scales with degree; a pre-seed timing tells
  you nothing).

## 7. Non-goals (v1)

- **No overlay registry refactor.** Board decorations today are ad-hoc
  always-on classes; this ships as one more prop + one more checkbox. If a
  third data-backed overlay arrives (PST heatmap from `PstGrid.tsx` is the
  obvious second), THAT is the moment to extract a registry — not before.
- **No per-witness opponent-Elo weighting** (§3, v2).
- **No fold-based "would play"** inference. v1 claims only what is witnessed:
  "did play, in recorded games." Extrapolating a player's likely choice in an
  unrecorded position is a generation/steering problem, out of scope here.
- **No new storage.** Zero writes, zero schema; the read rides evidence rows
  that exist the moment the corpus lands. Consensus accumulates at ingest —
  no backfill path exists and none is added.

## 8. Open decisions for the implementing session

1. `p_players bytea[]` vs. having the SQL call `chess_ranked` internally.
   Array-in keeps the function pure and testable, and lets the UI pin a custom
   roster later; taken as the default here, revisit if the extra round trip
   annoys.
2. Whether `/chess/explore` grows a `roster: true` mode instead of a new
   endpoint. New endpoint assumed (explore's response shape is already loaded),
   but merging is defensible.
3. Hue assignment: per-move rank hue (suggestion-mark ramp) vs. stable per-move
   hash hue. Rank hue assumed for consistency with the existing legend
   ("weaker → stronger" ramp already trained the user's eye).
4. Where the roster N surfaces in the UI (fixed 20, or a small stepper).
5. **Roster era scope.** A single Glicko fold over 126 years ranks Lasker
   against Carlsen on incommensurable opponent pools — rating is relative to
   the pool that produced it, and the pools never met. Options: (a) window
   the roster to a modern era by default (e.g. games since 2000); (b) an era
   selector in the UI feeding different rosters into `p_players` — the
   array-in design (§8.1) already supports this with zero SQL changes, which
   is an argument for keeping it; (c) accept the all-time fold and label it
   honestly. Needs an operator ruling; interacts with the testimony-semantics
   questions already open in #447/#449.
6. **Ship before or after #451.** After: the read is trajectory-derived, the
   full corpus is in play, nothing is thrown away — but the feature waits on
   a B-series schema law with operator sign-off pending. Before: requires
   the interim roster-scoped analyze (§6.3) writing per-ply rows the #451
   migration will re-shape. This is the operator's call; the doc's default
   is AFTER (record the corpus meanwhile — §6.2 — so derivation is ready to
   run the moment the law lands).
7. **Selective-analyze mechanism** (interim path only, §6.3): CLI
   header-based pre-filter vs. a roster-scoped analyze command over
   already-recorded games. The second composes better with the record-first
   strategy (record once, analyze subsets by marker), but requires the
   recorder lane to have seen the games first.
8. **Presence vs. choice share** as the displayed stat (§5): share is the
   honest number at dense coverage but needs true per-position totals to
   survive `p_limit` truncation — decide whether totals ride the SQL function
   or a second cheap read.
