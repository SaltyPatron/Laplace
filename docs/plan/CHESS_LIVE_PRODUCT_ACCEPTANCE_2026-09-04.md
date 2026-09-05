# Chess live-product acceptance — 2026-09-04

This document pins the deployed failures observed during the 2026-09-04 product pass so they do not get lost across narrow fixes, reseeds, deployments, or agent hand-offs.

It is an **acceptance matrix**, not a substitute for the governing issues. Each row names the owner issue/PR and the live proof required before the failure is considered closed.

## Closure law

- Source inspection is not live acceptance.
- A green unit test is not deployed-product acceptance.
- A successful request is not proof that the requested provider data was actually acquired or persisted.
- A legal chess move is not proof that the Chess Forward Pass is using the available chess substrate.
- A benchmark implementation is not proof that the deployed host uses its intended concurrency or exposes useful progress.
- Do not close a row merely because a neighboring symptom disappeared.

Current `main` when this matrix was started includes:

- #1502 — standings scope repair + removal of Chess Lab/Lichess operator-token gate;
- #1497 — durable/indexed FIDE estate + bounded-parallel Stockfish census preparation/checkpoint work.

Those merges still require the live checks called out below.

---

## A. Player standings / ranking

**Observed failure:** the player detail page could show plausible per-player game data while `/chess` standings produced absurd ratings and ordering because unrelated `OUTCOME` carriers were admitted into the ranking surface.

**Owner:** #1502 (merged)

**Required live proof:**

- player list uses only the governed `(Chess_Player, OUTCOME, Chess_Result)` standing cell;
- ascending and descending sorts are exact inverses over the same arena;
- game/rating/RD/strength sorts do not mix unrelated relation carriers;
- representative known players reconcile list values with their detail page;
- profile-only identities remain valid identities but are visibly distinguishable from game-rated identities;
- no overflow/garbage values reappear under any sort direction.

---

## B. Player database search semantics

**Observed live regression:**

```text
query "Nakamura, Hikaru" -> one profile-only identity, 0 games, neutral 1500 ±350
query "Hikaru"           -> separate game-witnessed Hikaru identity (~2.7k games)
query "Nakamura"         -> baxboynakamura only
```

The Player database UI explicitly promises surname, full-name, and close-spelling search. The exact-identity anti-contamination repair was over-applied to that generic browse operation.

**Owner:** #1398; product browsing contract also relates to #1404.

**Required split:**

- **exact provider/account lookup**: exact content-addressed resolution is terminal and never fuzzy-expands;
- **player database search**: bounded alias/name/trajectory candidate discovery runs even when one literal form happens to be an exact entity; exact hits may rank first but do not suppress other candidates;
- candidate discovery never creates identity edges or merges profiles.

**Required live matrix:**

```text
Nakamura, Hikaru
Hikaru
Nakamura
MagnusCarlsen
Magnus Carlsen
Karpov
Fisher
```

Verify candidate membership, ranking/relevance, and the declared lookup mode for every query.

---

## C. FIDE discovery, roster facts, and exact-profile enrichment

**Observed failure:** `Ingest FIDE top players` reported `100 official FIDE profiles ingested`, then still offered one-at-a-time `FIDE · Import profile` actions. Inspection showed the bulk path had admitted rating-list-derived profile objects but had **not** fetched the selected players' exact FIDE profile pages; the one-at-a-time button used the same rating-list conversion.

**Owner:** #1445. Durable publication/read performance is #1479 / #1497.

**Required provider boundary:**

```text
published FIDE rating estate
-> select/rank exact provider IDs
-> admit rating-list facts with publication provenance
-> bounded-parallel exact-profile enrichment when requested
-> per-ID success/failure receipt
-> bulk substrate apply/readback
```

**Required counters:**

```text
selected
rating_rows_admitted
profiles_requested
profiles_fetched
profiles_enriched
already_present
failed
```

`profiles_ingested = requested objects` is not an acceptable success receipt.

**Required live proof:** one top-N run enriches all requested IDs or names every failed ID without manual row clicks. The row action is hidden/disabled/relabelled according to actual enrichment state.

---

## D. Preserve all useful source chess data

**Observed failure:** Lichess/live game data existed, but clocks and richer per-ply annotations were absent from product readback.

**Owner:** #1503.

**Requirements:**

- request optional provider fields that are available and useful (for example Lichess clocks);
- preserve source PGN tags/comments/NAGs/custom fields rather than dropping unknown-but-valid source data;
- preserve provider game metadata, time control, ratings, termination, source game id, timestamps, etc.;
- preserve per-ply clock/think-time observations when supplied;
- preserve `ChessComment` and `ChessAnnotation` lanes through the public readback surface;
- expose Laplace/Stockfish eval/depth/nodes/PV/motifs where those calculations actually occurred;
- source observations and derived/calculated annotations remain distinguishable;
- partial clock testimony remains partial: one missing clock does not null the whole lane and unknown is never rewritten as zero.

Apply the same source-fidelity law to Lichess, Chess.com, FIDE, arbitrary PGN, Cutechess/engine PGN, books/annotations, and future providers.

---

## E. Chess Lab / Lichess auth scaffolding

**Observed failure:** Chess Lab/Gauntlet/Lichess retained a second browser operator-token/HTTPS gate that did not match the intended product auth model.

**Owner:** #1502 (merged).

**Required live proof:** no Chess Lab, Gauntlet, Import, or Lichess control asks for or depends on the obsolete shared operator token. Future auth belongs to the normal product tenancy/auth boundary rather than route-private scaffolding.

---

## F. Play session continuity / sudden 404

**Observed live failure:** an in-progress browser game suddenly produced:

```text
bot move failed: 404 Not Found
```

**Owner:** #938.

**Cause to preserve in tests:** server routes correctly return HTTP 404 for absent/foreign sessions, but the browser only rebuilds a session after a successful JSON body saying `session expired`. `apiPost` throws on the actual 404, so that recovery branch is unreachable.

**Required behavior:**

- retain server-side 404 for absent/foreign session isolation;
- Play UI catches the session-missing 404 for its own current game, rebuilds one server session from the browser's saved move history, and retries the original move/bestmove exactly once;
- stale session id is replaced atomically;
- a second failure is surfaced rather than looped;
- foreign-tenant sessions remain inaccessible;
- restart/deploy/session-loss E2E preserves repetition, castling, en-passant, halfmove clock, and move history.

---

## G. Draw avoidance and history-aware search

**Observed failure:** Laplace can repeat/draw when a winning continuation should be preferred.

**Owners:** #938 for authoritative game history; #833/#1419 for action/search policy.

**Critical current split:** `ChessModality.Terminal(ChessState)` knows threefold, 50-move, insufficient material, mate and stalemate, but `Search.Think` takes only `Board`. Connected play keeps a `ChessState` for recording and then discards its repetition history when invoking search.

**Required law:**

- search receives a history-bearing state/coordinate, not only a FEN/Board snapshot;
- descendant history updates use the same reset/append law as normal game application;
- exact terminal W/D/L is evaluated before heuristic scoring;
- strongest-play outcome order is win > draw > loss;
- draw is not merely an arbitrary `0cp` heuristic tie;
- when losing, an available draw may rationally outrank a forced loss;
- tablebase WDL/DTZ and ordinary rule draws use compatible outcome semantics;
- interior search transpositions are not falsely treated as played-game threefold.

**Required fixtures:** winning side avoids repetition/stalemate, losing side takes a draw, pre-root threefold history, 50-move threshold, and ordinary transposition control.

---

## H. One-ply popularity is an explorer, not chess cognition

**Observed failure:** the Play/analysis presentation exposed effectively one-ply candidate information and many moves appeared driven by popularity while ignoring opponent replies/threats.

**Owners:** #833, #1419, #1401.

`ChessEngineService.ExploreAsync` is legitimately an **observational current-position continuation/repertoire** read. It primarily ranks global continuations by witness count and player-scoped continuations by player game count. That answers `what was played here?`, not `what should I play?`.

**Required separation:**

- Explore/repertoire is explicitly labelled as one-step observational evidence;
- Play/UCI/Lichess/bestmove use the common Chess Forward Pass;
- observed popularity/usage, observed outcome standing, deterministic/tactical calculations, opponent-response search, and final selection are separate receipt fields;
- a popular move that loses tactically must be rejected by the action path even if Explore still reports it first;
- observational evidence may legitimately break a tie among tactically sound moves under the selected goal/context;
- `plies searched` must report actual search, never the number of observational continuation steps displayed.

---

## I. Chess Forward Pass strength

**Observed live result:** Gauntlet produced 0 wins / 0 draws / 10 losses against Stockfish capped at 2850 at depth 6, with Stockfish mating Laplace from both colours.

Ten games do not estimate Elo, but they are enough to reject any claim that the deployed player is already strong.

**Owners:** #833, #1419, #834.

**Required completion:**

```text
current board/game trajectory
-> exact legality/terminal/material/tactics
-> candidate frontier
-> opponent responses
-> typed structural calculations
-> selected game/player/time/opening/motif/shape/book/lexical providers
-> uncertainty/standing
-> descendant search/steer
-> select
-> witness consequence
```

Do not "fix" this by merely increasing alpha-beta depth or building a parallel Stockfish/NNUE clone. The acceptance target is that Laplace's already-admitted typed knowledge/calculations actually participate in the common action program.

Gauntlet receipts must state explicit Laplace substrate/program mode, evidence epoch/provider set, resource budget, opening boundary, and actual search metrics. Do not depend on an invisible environment default.

---

## J. Stockfish census ETL / no 30-minute black box

**Owners:** #1497 for the recent worker-wave/checkpoint repair; #1430 for canonical calculation-grain semantics; benchmark/perf boundary #1438.

#1497 addresses the measured serial-engine failure by introducing an explicitly parallelizable preparation stage, topology-sized engine wave, shared-position single-flight, incremental cache journal, bounded engine deadlines and resumable complete-line publication.

That implementation is not accepted until the deployed host proves it.

**Required live proof:**

- >1 active Stockfish worker on a multi-core host;
- topology grant/worker count is printed;
- early non-zero durable progress;
- repeated canonical positions pay engine work once under the same calculation generation;
- cancellation preserves completed engine work and resumes without duplicate semantic support;
- wall time / evals-per-second / CPU utilization are measured against the prior ~29-minute `0/9688` failure.

**Remaining observability defect:** `scripts/ingest-source.sh` still redirects the full Actions ingest stream to a runner-local detail file. Long census steps therefore still can look dead from the Actions console.

A bounded heartbeat must expose at least:

```text
lines discovered/completed
unique positions
cache hits/misses
active/target engine workers
evals/sec
durable checkpoints
applied/deposited units
elapsed / ETA when meaningful
```

Rate-limit the heartbeat; do not replace silence with unbounded debug spam.

---

## K. Stockfish calculation semantics

**Owner:** #1430.

Performance caching is not enough. A deterministic Stockfish calculation is keyed by canonical position/transition + exact Stockfish calculation generation/recipe, not by each PGN LINE that happened to contain it.

- many real game occurrences remain many observations;
- one Stockfish generation's result remains one calculated provider result;
- cache identity binds binary/build/network/options/budget;
- move-quality identifies the exact canonical transition;
- crash/retry does not increase semantic support for already-completed calculation units.

---

## L. Cross-surface parity

The same logical operation must not silently mean different things depending on route.

Required parity checks across web/API/CLI/UCI/Lichess/MCP where applicable:

- exact identity lookup;
- player browse/search;
- game replay + metadata/annotations;
- Explore/repertoire;
- bestmove/action;
- terminal/history handling;
- provider/evidence receipt;
- ranking arena/sort semantics.

A route may render differently, but it may not invent private semantic rules.

---

## Live acceptance checklist

Do not declare the 2026-09-04 chess pass closed until all applicable boxes below have a deployed receipt:

- [ ] rankings/list reconcile with player details and sane sort directions (#1502)
- [ ] generic player search finds surname/full-name/close-spelling candidates without identity contamination (#1398/#1404)
- [ ] FIDE top-N distinguishes roster facts from exact-profile enrichment and requires no manual N-click workflow (#1445)
- [ ] Lichess/Chess.com/PGN clocks + metadata + annotations survive ingest/readback (#1503)
- [ ] obsolete Chess Lab operator-token gate is absent everywhere (#1502)
- [ ] Play survives server/session loss without a user-visible dead-end 404 (#938)
- [ ] search consumes real game history and demonstrates rational draw behavior (#938/#833/#1419)
- [ ] Explore one-ply popularity is visibly distinct from action/search reasoning (#833/#1419)
- [ ] strongest-play path proves opponent/threat/tactical/terminal handling beyond popularity (#833/#1419/#1401)
- [ ] Gauntlet emits an explicit, reproducible Laplace program/evidence/resource receipt (#834/#833)
- [ ] Stockfish census proves concurrent workers, incremental durable progress, bounded failure/retry and live console heartbeat (#1497/#1430/#1438)
- [ ] Stockfish calculated testimony is deduplicated at canonical calculation grain (#1430)

When a box is closed, link the exact deployed run/query/screenshot/receipt in the owning issue rather than editing this matrix into a claim without evidence.
