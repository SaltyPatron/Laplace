# 44 — Chess forward pass vs industry engine encyclopedia (2026-08-03)

Status: **campaign framing log** (append-only). Not binding law — binds *to*
specs 11 / 15 / 33 / 36 / 37 + W14 + epic #818. Authority for open work: GH issues.
Written after Gemini Deep Research on “2026 top-tier chess engines” was filtered
through operator corrections in session.

## 0. Category error this document kills

Treating **how to compute a move** as the entire engine is a category error.

Standard engines (Stockfish, Lc0) are ephemeral calculators: build a tree, score
leaves (NNUE / HCE / V-head), pick a move, discard world memory. They have no
attestation graph; they re-infer positional quality every ply.

Laplace chess is a **program over the substrate ISA** (spec 37) with chess ROM
(decomposer/realizer + board ladder). Move gen + αβ/PVS live inside one stage —
`PROPOSE` — of a multi-stage **Chess Forward Pass** (spec 36: “play a move” is
one program over the same primitives as answer / translate / …).

**Unfinished ≠ wrong. Unfinished ≠ won’t work.** Implementation completeness
is schedule. Architectural validity is separate. Spec 37 PROPOSED / opcodes
unconsolidated / missing chess perfcache refute *completion*, not the processor
model (#823).

## 1. Chess Forward Pass ↔ industry mechanics

| Pass stage | Stateful processor primitive (attestation surface) | Industry substitute / role |
|---|---|---|
| **RESOLVE / COMPOSE** | Merkle position identity, tier addressing (`ChessCompose`), content-addressed DAG | Ephemeral Zobrist for TT indexing (not identity) |
| **ORIENT / ELECT** | Task orientation (explore, emulate player, Syzygy convert, opening leave) | External UCI bounds (`go depth` / `go nodes`) |
| **SCAN / WEIGHT** | Multi-source consensus (`eff_mu`, RD, witnesses), lane trust, provenance duality | Single-source static score / NNUE / V-head |
| **TRAVERSE / SEQUENCE** | Shape peers, line trajectories, distance-to-Syzygy, missed finishes, motif walks | Unstructured game-tree path only |
| **PROPOSE** | Legal movegen + selective search / deepen | Minimax/PVS, magic/PEXT movegen, qsearch — **Gemini’s main body** |
| **STEER** | Re-rank candidates by live observational frontier + fold | Absent as attestation STEER; tree move-ordering is still PROPOSE |
| **SAMPLE** | Select under RD / time / risk | Argmax score or policy softmax |
| **REALIZE** | Id → UCI/SAN | Direct move formatting |
| **WITNESS / FOLD** | Deposit ply + outcome; Glicko cells + learned PST update | Offline retraining (PyTorch / self-play datasets) |

Superhuman strength on this project = complete that pass (fast selective PROPOSE
+ honest live STEER + continuous WITNESS), **same project** — not a Stockfish
clone beside Laplace.

## 2. Gemini encyclopedia filter

Every industry technique maps to one of:

| Bucket | Examples | Action |
|---|---|---|
| **Industry substitute for missing memory** | NNUE (SFNNv10), heavy HCE, PolyGlot-as-primary-identity, Syzygy-only-as-live-probe | Do not treat absence as “invalid architecture.” Laplace substitutes observational memory (fold, LINEs, closings catalog). A net would be a *source/eval term in the loop* if ever added — not Project Two. |
| **PROPOSE accelerator** | PVS, aspiration, LMR/LMP, NMP, futility/razoring, ProbCut, SE/MC, history/CorrHist, Lazy SMP, lockless TT, BMI2/magic movegen | Valid Elo work *inside* PROPOSE; feeds richer candidates to STEER. Not a competing paradigm. |
| **Validation science** | SPRT, pentanomial, OpenBench/Fishtest, cutechess-cli | Non-optional for proving strength patches. Lab has cutechess embryo (#604). |
| **Already Laplace-shaped (different packaging)** | Opening books → LINE entities (#546/#818); Syzygy → `HAS_WDL`/`HAS_DTZ` catalog (#605); self-play → ChessSelfPlay WITNESS | Compose over ISA (#820); don’t reinvent sibling engines. |

## 3. Unique observational metrics (what Gemini cannot rediscover)

Installed surface (`SELECT * FROM api('chess')`, verified 2026-08-03): 24 ops
including `chess_moves`, `chess_player_moves`, `chess_opening_*`,
`chess_syzygy_line`, `chess_distance_to_syzygy`, `chess_missed_finish`,
`chess_opening_shape_peers`, `chess_time_pressure_outcome`, `chess_ranked`, …

First-class signals no classical engine has as SoR:

- MOVE consensus cells (`eff_mu`, RD, witness_count) across Magnus / books / openings / lab / self-play
- Provenance duality (fold aggregate + per-game `context_id`)
- Identity mesh (same position id across sources)
- Lane-separated testimony (record / analyze / stockfish census / self-play + trust)
- Outcome ≡ attestation enum (spec 11 / guide)
- Player-at-position repertoire; think-class × outcome; motif attestations
- Syzygy as world facts + missed-finish / distance-to-close
- Shape peers (geometry); learned PST from fold

These are ORIENT / SCAN / WEIGHT / TRAVERSE / STEER inputs — not “nice SQL beside UCI.”

## 4. Current wiring (completion audit — not validity)

Honest truncated pass today:

1. COMPOSE position (modality) — **built**
2. Classical PROPOSE (`Search`: ID + αβ + TT + killers + MVV + qsearch + SEE; PeSTO + learned PST) — **built, pre-modern selective search**
3. STEER — **thin straw**: `SubstrateRootBias` / `SubstructureFoldBias` clamp ±150cp @ 8cp/point (`SubstrateRootBias.cs`)
4. REALIZE UCI — **built** (`laplace-uci`, Substrate fold/edge/off)
5. WITNESS — **built** on lab / Lichess / Play paths; proof thin (#604/#495/#821)

Firmware ROM (spec 33): `laplace_t0_perfcache_17.0.0.bin` + `laplace_highway_perfcache.bin` **live** on host.
**No chess perfcache** (CONSOLIDATION; #822 is the position→coord floor slot).

ISA consolidation: G1/G3/G7/G8 ratchets landed; opcodes unfinished (#758 / CHECKPOINT Phase 6).
Unfinished consolidation ≠ wrong architecture (#823).

### Elo floor measurement

- Operator prior: ~2100 vs strength-capped Stockfish (lab default `UCI_Elo=2000`).
- Harness: `CutechessRunner` + `laplace chess match` — code present.
- Re-measure started 2026-08-03 on this host: `laplace-uci` (Release, substrate fold active) vs Stockfish 14.1 `UCI_Elo=2000`, `st=1`, 20 rounds → `/tmp/laplace-vs-sf2000/`.
- Mid-run snapshot (~10/20): Laplace **0–8–2** (score ~0.10). **Do not treat as final.** Conditions may differ from prior confirmation (SF version, time control, corpus/fold state, root-bias fix era — see `.scratchpad/04` suspect Elo note). Finish the match; stamp result here and on #818.

## 5. Distance (one project)

```
Truncated pass (~club / prior ~2100 claim)
  → denser + honest SCAN/WEIGHT (#447/#449)
  → STEER reads full observational frontier (not ±150 straw)
  → PROPOSE accelerators as needed
  → WITNESS loop proven (#604/#821)
  → converse/MCP resolve into mesh (#575)
  → ISA-composed chess surfaces (#820) + chess ROM floor (#822)
  → superhuman bar (same machine)
```

Gemini’s ~3400 industry bar remains the **capability yardstick**. The road is
finishing the chess forward pass on the substrate processor — not a second engine.

## 6. Issue map (actions this session)

| Issue | Role under this frame |
|---|---|
| #818 | Parent epic — modality stress + ISA; absorb forward-pass thesis |
| #823 | Processor / firmware docs; link this log; validity≠completeness explicit |
| #820 | TRAVERSE/SEQUENCE chess surfaces as ISA programs |
| #821 | Live proof of deposited catalog (WITNESS/SCAN density) |
| #822 | Chess modular perfcache slot (position→coord) under spec 33 |
| #605 | Syzygy closings = TRAVERSE facts, not probe-oracle product |
| #575 | RESOLVE into mesh from converse/MCP/FEN |
| #447/#449 | Honest WEIGHT/STEER inputs (dual-critical) |
| #604/#495 | Closed WITNESS loop measurement |
| #512/#547 | Geometry / trajectory for SEQUENCE |
| **new** | Chess forward pass composition: UCI/play as full pass (STEER surface) |

## 7. Gemini report disposition

Retain as external encyclopedia. Score every section with §2 buckets.
Do not maintain a parallel “become Stockfish” backlog.
PROPOSE-accelerator work is legitimate when authorized as strength engineering
on the same pass — never as proof that the architecture was wrong.

## 8. MAJOR GAP — driving / measuring Laplace as it exists (not Stockfish defaults)

**Symptom (this session):** agent ran cutechess with lab defaults
(`st=1`, Stockfish `UCI_Elo=2000`, no openings book, no substrate-test, no
explore of the live frontier) and treated that as “seeing for yourself.”
Mid-run crushed Laplace. That recipe measures **truncated PROPOSE under a
generic TC against capped SF**, not whether the observational SoR is being
used well.

**Live proof the SoR is not “defaults”:**

`POST /chess/explore` startpos (host `:8080`, 2026-08-03):

| move | effMu | rd | witnesses |
|---|---|---|---|
| Na3 | **137.96** | 49.2 | 114 |
| e4 | 39.27 | 11.1 | **305376** |
| Nf3 | 31.85 | 8.6 | 63582 |
| … | | | |

Raw MOVE μ ranks junk first — exactly #447. UCI `Substrate=fold` + learned PST
@ `movetime 2000` still played **e2e4** (depth 6); `Substrate=off` played
**g1f3** (depth 7). Fold STEER ≠ raw `chess_moves` order (`SubstructureFoldBias`
reads OUTCOME folds on substructures+position; `edge` reads MOVE-edge μ).
Player filter `magnuscarlsen` returned `playerGames: 0` on this host — repertoire
path not exercised by defaults either.

### What “take advantage of Laplace now” actually means

| Lever | Where | Why it matters |
|---|---|---|
| **`substrate-test --mode fold --openings [--learned]`** | CLI / lab | Designed honest test: guided vs pure at **matched depth**, openings where corpus has ECO coverage. Not cutechess-vs-SF. |
| **`--mode edge` vs `fold`** | UCI `Substrate` / CLI | Edge = poisoned popularity at startpos; fold = substructure OUTCOME generalization. Measuring the wrong mode “disproves” nothing. |
| **`--openings` / OpeningSeed** | `/vault/Data/Games/Chess/openings/*.tsv` | Random starts are where the graph is thinnest; ECO suite is where SCAN has density. |
| **`--learned` on substrate-test** | CLI flag (UCI always blends learned PST) | CLI defaults omit learned PST; UCI includes it — apples/oranges if mixed. |
| **`--cp-per-point` / `--cap`** | CLI only (UCI hardcodes 8 / 150) | STEER straw strength; UCI exposes no knobs. |
| **`/chess/explore` + player** | HTTP `:8080` | Read the frontier before trusting a match score; player repertoire is a different STEER input. |
| **Depth-matched or movetime budgets** | Prefer over blind `st=1` | `ParseGo`: `st`/clock → `myTime/30`; depth mode has 120s ceiling. SF@2000 under `st=1` is not a calibrated “2100 protocol” until stamped. |
| **Syzygy / shape / motifs / think-class** | SQL `api('chess')` | Present as TRAVERSE facts; **not wired into UCI STEER at all** today. Using “Laplace” without these is using ~5% of the eyes. |
| **WITNESS loop** | lab ingest / Lichess / Play | Strength compounds only if games deposit; throwaway cutechess without ingest wastes the pass close. |

### CutechessRunner gaps (product, not operator ignorance)

- No `-openings` / book suite wiring
- No `option.Substrate=fold|edge` explicit pass (relies on engine default)
- No cp/cap UCI options
- Default `st=1` + SF Elo 2000 is a **watchable demo**, documented as such — not the scientific protocol
- Does not consult explore / player / TB surfaces

### Issue

#834 — measurement & drive protocol: use the observational pass, not Stockfish-shaped defaults.
