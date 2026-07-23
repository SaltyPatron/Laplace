# The chess modality — reference

What exists, what each piece does, how to drive it, and how it all closes the
loop. Written 2026-07-23. Counts cited here are as-of that date — verify live
(`SELECT * FROM api('chess')`, `substrate_counts()`); the generated
[docs/INVENTORY.md](../INVENTORY.md) owns every countable fact. The operational
lab how-to is [chess-lab.md](chess-lab.md); the binding spec is
[specs/11](../specs/11_Chess_Provenance_Consensus_Spec.txt).

## Why chess exists here

Chess is the proving domain: its ground truth is objectively checkable, and its
outcome enum `{Loss=0, Draw=1, Win=2}` is bit-identical to the attestation
outcome — the same Glicko-2 fold that rates chess moves rates every epistemic
claim in the substrate. Everything below is therefore a working, measurable
instance of the general invention: content-addressed identity, witnessed
evidence with provenance, and a rated consensus you can walk.

## Identity — the one law that bites

- A **position** is a composed entity: its id comes from the merkle composition
  of its canonical surface (`ChessCompose.Position`), NOT from hashing the
  surface string. `canonical_id('<surface>')` gives a DIFFERENT id and finds
  nothing. External callers get position ids by composing through the modality
  (the `/chess/explore` endpoint does this from a FEN) — never by hashing.
- The canonical **surface** (`PositionContent.Surface`) encodes side to move,
  castling, en passant, every piece placement, pawn structure and material —
  reconstructible back to a FEN (`TryFenFromSurface`; move counters excepted).
- A **game** id hashes `white|black|date|moves` (7-tag extension tracked in
  #513). A **player** id hashes the alias-canonicalized name
  (`PlayerAlias.Canonical`: "Carlsen, Magnus" ≡ "Magnus Carlsen"; chess.com's
  "MagnusCarlsen" canonicalizes to "magnuscarlsen").
- Same content = same id across sources: a book line, a Magnus game, and a lab
  self-play game that reach the same position all land on ONE position entity.
  Cross-source agreement is a hash collision at one consensus cell — that IS
  the mesh.

## The three lanes (record vs calculate, spec 08)

| Lane | Source | What it deposits |
|---|---|---|
| **Recorder** | `ChessPgn` (also `ChessBook` for embedded games) | WITNESSED, game-grain only: header facts (`HAS_WHITE/BLACK/EVENT/ECO/RESULT/RATING/TIME_CONTROL/…`) + ONE verbatim `HAS_MOVETEXT` edge carrying the whole record (clocks, evals, comments, NAGs survive inside it — the lossless law). No replay, no geometry, no per-ply rows. |
| **Analyzer** | `ChessAnalysis` (versioned, marker-gated) | CALCULATED, from replaying the witnessed movetext: position/substructure entities + geometry, `MOVE` edges (subject position → object position, per-game `context_id`), `OUTCOME` deposits that carry the game result into the fold, openings (`GAME_HAS_ECO/OPENING` via longest-prefix ECO match), motifs (`GAME_HAS_MOTIF`: forks, discovered checks, hanging pieces, named traps), clocks→think-class (both lichess remaining-clock and cutechess spent-time dialects), PGN `[%eval]` evals, annotation-glyph move quality. |
| **Stockfish census** | `ChessStockfish` (versioned, marker-gated) | CALCULATED: per-position `HAS_EVAL` (side-to-move cp, depth-10 v1 budget) and eval-delta `MOVE_QUALITY` (loss ≥300cp blunder / ≥100 mistake / ≥50 inaccuracy; silence = fine move). One search per UNIQUE position (run memo + persistent cache); deposits stay per-game (provenance never mashed). |

Self-play and the Lichess bot deposit through a fourth source (`ChessSelfPlay`,
Response-class trust — outranked by design) via the live game host: every ply
folds immediately, so the next move's bias reads what the last game taught.

## The consensus you can read

Every `(position, MOVE, position')` cell folds all its witnesses — Magnus's
games, books, openings tables, lab self-play — into one Glicko-2 rating;
`eff_mu = rating − 2·rd` is the conservative estimate everything ranks by.
Evidence rows keep per-game `context_id`, which is what makes player-scoped
questions answerable without any per-player storage.

As of 2026-07-23 (verify live): ~9.5k recorded games (chess.com Magnus corpus +
lab + books), ~738k MOVE consensus cells, 3.7k openings, ~19k motif rows, and
the full census — ~2.76M stockfish deposits; 23,102 blunders / 98,303 mistakes /
120,328 inaccuracies corpus-wide.

## The read surface

SQL (discover with `SELECT * FROM api('chess')`; `SET search_path=laplace,public;`):

```sql
-- Opening explorer: μ-ranked continuations out of a position
SELECT * FROM chess_moves(:position_id, 12);

-- Player repertoire: that player's actual continuations, games + score,
-- resolved by color (side to move of the position decides which color is theirs)
SELECT * FROM chess_player_moves(:position_id, :player_id, true, 12);

-- Typed batch consensus lookup (prunes to ONE relation partition;
-- the untyped form Append-scans every partition — never use it on a hot path)
SELECT * FROM consensus_by_ids(:edge_ids, relation_type_id('MOVE'));

-- Corpus blunder census (object ids are composed word ids — word_id(), not canonical_id())
SELECT q.tok, count(*) FROM (VALUES ('blunder', word_id('blunder')),
  ('mistake', word_id('mistake')), ('inaccuracy', word_id('inaccuracy'))) q(tok, oid)
JOIN attestations a ON a.object_id = q.oid
 AND a.source_id = (SELECT id FROM canonical_names
                    WHERE name='substrate/source/ChessStockfish/v1')
GROUP BY 1;
```

HTTP (the deployed API; all under `/chess/*`, tags `chess` / `chess-lab`):

| Endpoint | Does |
|---|---|
| `POST /chess/explore` `{fen, player?, limit?}` | Opening explorer / repertoire from a FEN: composes the position id, returns SAN-decoded moves with `effMu` (points, mover-positive), `witnesses`, and `playerGames`/`playerScore` when a player name is given |
| `POST /chess/legal` / `/chess/move` / `/chess/eval` / `/chess/bestmove` | Board service: legal moves μ-scored, apply a move, evaluate, search (with `substrate:true` the search is consensus-biased) |
| `POST /chess/play/*` | Human-vs-engine sessions; finished games deposit as ChessSelfPlay witnesses |
| `POST /chess/lichess/start|stop`, `GET /chess/lichess/status` | The Lichess bot (accepts challenges, plays with per-ply fold + recording, posts move commentary) |
| `POST /chess/lab/start`, `GET /chess/lab/jobs/{id}/events` (SSE), `/artifact`, `/ingest` | The lab: see [chess-lab.md](chess-lab.md) |

Web (SPA, chess tabs): **Play** — board vs engine with the Explore panel
(ranked continuations for the current position, player filter, click-to-play);
**Chess Lab** — job runner with a live spectator board streaming every ply of
cutechess and in-process self-play games.

UCI: `laplace-uci` is a standalone engine any GUI can drive — alpha-beta whose
root ordering is biased by live consensus (`Substrate` option: `fold` /
`edge` / `off`), with a learned piece-square overlay folded from recorded
games. Degrades to pure search if the DB is unreachable.

## Feeding it (CLI; on Linux `scripts/ingest-source.sh <source> [path]`)

```
cli chess fetch <user> [--site chesscom|lichess]   # monthly archives → one PGN
cli ingest chess <pgn-or-dir>                      # record + analyze (chained)
cli ingest chess-analyze [--depth N]               # re-derive calculated layer
cli ingest openings <eco-tsv-dir>                  # ECO opening tables
cli ingest chess-books <txt-dir>                   # plaintext books: record+derive
cli ingest chess-eval [--depth N | --nodes N]      # stockfish census (see below)
```

Every lane is idempotent (content addressing + per-game/version markers): re-runs
skip complete work; a killed run resumes where it stopped.

## The census, durability, and cost

`chess-eval` is a **seed step** (`seed-chess.yml`, choice `chess-eval`): a
`db-reset` + reseed re-derives the census exactly like every other layer —
derived state is durable because the ladder can rebuild it, never because rows
are precious. Cost controls, in layers:

- **Unique-position memo** — a stockfish value is a pure function of (position,
  budget); each unique position is searched once per run no matter how many
  games share it (the start position appears in every standard game).
- **Persistent eval cache** — the memo survives the process as a versioned,
  budget-pinned blob (`LOCALAPPDATA/laplace/chess-eval-cache.bin`, env
  `LAPLACE_CHESS_EVAL_CACHE`; spec-33 two-tier pattern). After one warm census,
  a post-reset rebuild pays no engine time — only the substrate write path.
- **`--nodes N`** — node-capped search: bounded worst case (depth budgets have
  unbounded tails on sharp positions), reproducible testimony. Opt-in; the v1
  census budget is depth 10 and stays so until #508 REPLACE semantics let a
  budget change ride a version bump.
- `LAPLACE_INGEST_MAX_UNITS=N` bounds any run for smoke tests.

Reference numbers (this host, 12 cores): full 9.5k-game census ≈ 50 min cold;
warm-cache re-census is write-path-only.

## The loop, closed

Lab and Lichess games fold as they are played (ChessSelfPlay, outranked trust) →
the UCI engine's bias and learned PST read the updated consensus for the next
game → finished cutechess PGNs auto-ingest → the analyzer and census lanes pick
up new games by marker → the explorer and census queries reflect all of it.
Evaluation IS ingestion; every match played teaches the next one.

## Known gaps (tracked; do not rediscover)

- Board geometry ladder + mantissa game trajectories are unbuilt — positions
  ride codepoint geometry (#512, #547). Blocks true position-similarity reads.
- Consensus modeling questions — self-play trust weight, adjudicated-draw
  testimony, drawish-book poisoning (#447, #449). This is why raw μ from the
  start position ranks offbeat first moves above 1.e4: the fold is dominated
  by self-play/blitz outcome testimony. Operator ruling needed before changing
  testimony semantics.
- Books: diagram/OCR extraction and a document-containment read for prose
  (#574); the 12 GM books are resident whole in the document lane, with only
  parseable games/lines attested (the `EXPLAINS` bridge).
- Chess is not reachable from converse/chat/MCP — FEN-shaped prompts don't
  resolve to position entities yet (#575). Read phase 2 (motif queries,
  book-vs-practice contrast, geometry similarity) is #576.
- `/chess/*` is unauthenticated (#489); write-path throughput at live DB scale
  is under investigation (#588).
