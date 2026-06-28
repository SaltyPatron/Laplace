# Chess engine + substrate fusion — roadmap & backlog (2026-06-27)

Everything shaken out across the "make the chess side sing" build. Status legend:
**✅ done · 🔜 ready (unblocked) · ⛔ blocked (dependency) · ❓ decision needed**. Ownership:
**[E]** = engine lane (`Laplace.Modality.Chess`, `Laplace.Chess.Uci`, `Laplace.Cli`, eval/search — this session);
**[P]** = provenance lane (`Laplace.Chess.Service`: `ChessGraph`/`ChessVocabulary`/`ChessPgnDecomposer` — parallel agent, in flight).

---

## 0. Validated state (the floor we're building on)

- **Classical engine ≈ 2105 Elo ±31** — pure C#, no DB/native in the hot loop. PeSTO tapered eval with
  toggleable `EvalTerm` overlays; negamax α-β + quiescence + Zobrist TT + iterative deepening + move
  ordering; Stopwatch time management; UCI + `info`. Measured: beat balanced Stockfish `UCI_Elo=2000`
  **305-158-37 (+105±31) over 500 games**, LOS 100%.
- **`laplace-uci.exe`** — standalone engine binary (no DB, fast startup). Speaks UCI for cutechess + lichess.
- **Local gauntlet** — `external/cutechess` submodule built (`scripts\win\build-cutechess.cmd`, Qt6.8.3 via
  aqtinstall → `D:\Qt`), driving `laplace-uci` vs `D:\stockfish`. Our own tooling, no third-party harness.
- **Openings decomposer** — built + tested (parses `openings/*.tsv` via the `pgn` tree-sitter grammar, replays
  all ~3,700 ECO lines 100%). NOT yet ingested.
- **Substrate root-injection seam** — `IRootBias` + `SubstrateRootBias` (raw edge) + `SubstructureFoldBias`
  (generalizing fold) + `laplace chess substrate-test --mode fold|edge|off [--openings]`.
  - raw `MOVE`-edge eff_mu = **NULL (−17)** — popularity, not strength (see §2).
  - **substructure-fold = FIRST REAL POSITIVE: +24 ±23 Elo / 500g** (opening-seeded from the 2,668 INGESTED
    ECO positions via `OpeningSeed` → `MatchRunner` openingFens). Generalizes via shared substructures; flips
    the null. Shared `SubstrateStateValuer` (fold extracted from `SubstrateTurnHost`); `ChessCompose.Gate`
    unifies the native-compose lock. 5/5 fold-math unit tests.
- **Overlay-ablation ladder** (`laplace chess ladder`, 120g/term, opening-seeded): Material +759, Pst +307
  (the two pillars), RookFiles +50, PawnStructure +26, Tempo +23, BishopPair +3 (≈0). ⇒ the 4 positional
  overlays add little individually = substrate-learn candidates; Material+PST are the floor.
- **Test coverage** — ~99 chess tests in `test-app.cmd` (Modality.Chess 84+ incl. eval/search/match,
  Chess.Service 9, Chess.Uci 8). Corpus: **~1M games / ~29M moves** → folded into 34.5M consensus relations.

---

## 1. The corpus & data model context

The substrate holds ~1M games / ~29M move-transitions, content-addressed: same position → same `Hash128` id
(`ChessCompose.PositionId`), so a move's learned value is an **indexed point lookup** (`consensus_pkey`).
Chess has ~10⁴⁰ positions, so the corpus is broad on **openings** but a thin slice everywhere else — which is
exactly why the first substrate test (random middlegames) came back silent.

---

## 2. Backlog by track

### Track 1 — Classical engine [E]
- 🔜 **Overlay-ablation Elo table** — finish `laplace chess ladder`: full eval vs each `EvalTerm` removed → each
  overlay's individual Elo. Cheap (parallel runner exists). *Gemini's "autopsy."*
- 🔜 **Climb the Stockfish anchor ladder** (vs `UCI_Elo` 2100/2200) to bracket the exact rating.
- 🔜 **Strength climb (optional):** more eval terms (mobility, king-safety, passed-pawn advancement); search
  upgrades (PVS, null-move, LMR, check extensions, SEE in quiescence); opening book; endgame tablebases.
- ⛔ **Cutechess-driven ablation** needs a UCI option to select `EvalTerm` (SPRT on overlays). In-process ladder
  avoids it.

### Track 2 — Substrate fusion (the differentiator) [E + reads P's data]
- ✅ Root-bias seam + `SubstrateRootBias` (raw eff_mu) → **null result** (corpus silent on random middlegames;
  raw eff_mu ≈ popularity, not strength — the spurious-correlation trap).
- 🔜 **Opening-suite-seeded matches** — seed games from real opening lines (corpus home turf); does the prior make
  it play better book? The fair test of book knowledge.
- 🔜 **Substructure-fold bias** — 2nd `IRootBias` over `SubstrateTurnHost.ValueStatesAsync` (folds OUTCOME over a
  position's substructures → generalizes to novel positions). The honest transfer test.
- ⛔ **Eval overlays → Glicko-2 attestations** — each overlay emits an OUTCOME attestation (own `ChessEval_*`
  source); self-calibrating trust = measured agreement with real outcomes. **Guardrail: start low trust, validate
  before raising** (the `SubstrateTurnHost:124-128` lesson). Needs P's source/trust plumbing.
- ❓ **Content-addressed position cache** (perfcache-for-positions, keyed by hash) → per-node substrate probes at
  TT speed → A*/best-first. The constraint was never "no substrate per-node" — it's "no live DB round-trip
  per-node." Bigger build; needs native thread-safety first.
- ⛔ **Native thread-safety** — `ChessCompose` native compose AV's under parallel (locked as a stopgap in
  `SubstrateRootBias`). Real fix gates per-node/parallel substrate use.

### Track 3 — Provenance & trust model [P, in flight]
- 🔄 Distinct sources (`ChessPgn`/`ChessSelfPlay`=Laplace/`ChessUserPrompt`/`ChessOpenings`) + real trust classes
  (stop `trustClassId: SourceId`).
- 🔄 Player entities + `HAS_RATING`/game + `PLAYED_BY`/move (parse `[White]`/`[Black]` — currently discarded).
- 🔄 `ChessGraph` rethread (thread source+trust+mover). **CRITICAL: `AppendMoveEdge` must allow a NULL player**
  (openings + self-play have no named mover).
- ⛔ **Time/clock/variant signals** — time-control class (bullet/blitz/rapid/classical), per-move clock
  (thought-out vs rushed; `[%clk]` is *stripped* by the grammar, like names were), time-remaining, game phase,
  variant (Chess960 = its own source, don't bleed into standard). Decomposer parse + weighting.
- 5 weighting axes: corpus trust · mover authority · outcome×surprise · pairing class · Glicko rank.

### Track 4 — Ingest & corpus
- ⛔ **Ingest the openings book** [E ready, P-gated] — decomposer built; run under the `ChessOpenings` source
  after the rethread.
- 🔜 **Opening-NAME capture** [E] — position ↔ "Najdorf"/ECO (the differentiated value of openings).
- 🔜 **Loop closure** [E] — every game-producer (cutechess `-pgnout`, lichess, self-play) → `pgn` grammar →
  substrate. Wire `cutechess → laplace ingest chess`. *The tree-sitter flywheel.*
- ❓ **Re-key / feature enrichment** — split position *identity* (placement/stm/cr/ep) from *features*, so
  PST-bucket / mobility / king-safety / open-file / bishop-pair / center tokens become learnable substructures
  **without orphaning the seed**. One-time re-key + re-ingest. **Biggest architectural decision.**
- ❓ **Data-driven eval** — distill the substrate's measured feature→outcome weights into the eval (learned PST
  replacing hand-tuned PeSTO). "Perfcache fuel."

### Track 5 — Verification & measurement [E]
- ✅ Beats-random; ✅ Stockfish Elo anchor (cutechess).
- 🔜 **Tactics EPD suite** (mate-in-N / WAC) — verification bar #2. Build as an **EPD tree-sitter grammar**, not a
  hand-rolled parser (consistent with the substrate; also loop-closure-friendly).
- 🔜 **Overlay-ablation Elo table** (Track 1).
- 🔜 **Self-play Elo ladder across checkpoints** as the substrate/eval evolve.

### Track 6 — Play & deploy [E, some P-adjacent]
- 🔜 **Lichess bot** (own HTTP Bot API client) — stream events/challenges, play, handle clock. Account is a BOT,
  token in `.env` `LICHESS_API` + `deploy\secrets\lichess.env`. → rated games = human-scale anchor + corpus.
  Unblocked (time mgmt done). **Outward-facing — user initiates go-live.**
- 🔜 **Wire the α-β `Search` into `/chess/*` endpoints + redeploy** — `ChessEngineService` still plays the depth-1
  `ModalityEngine`; swap in the 2105 engine so the web UI actually plays it. Then redeploy to IIS
  (`D:\Data\inetsrv\laplace-api`, see [[laplace-iis-deploy]] — web.config DB must = `laplace`).

### Crosscutting
- 🔜 **"Crazy-win" / game-review analyzer** — run search+eval over ingested games → centipawn-loss
  (inaccuracy/mistake/blunder) + surprise (won despite a bad move). Triage luck vs eval blind-spot (deep
  re-search) → blind spots are where the learned overlay must override PeSTO. Also a user-facing game-review
  feature. Touches Tracks 2 & 4.

---

## 3. Prioritization (value × effort × dependency)

**Phase A — now: unblocked, [E], high-value/low-effort. The honest measurements.**
1. **Overlay-ablation Elo table** — cheap, finishes Track 1, tells us what each hand-crafted heuristic is worth.
2. **Substructure-fold substrate test** — the *fair* version of the real test (does learned positional knowledge
   transfer to novel positions?). Medium effort, high signal.
3. **Tactics EPD suite + EPD grammar** — correctness gate #2 + advances the tree-sitter loop.

**Phase B — unblocked, [E], medium effort, high visible value.**
4. **Wire α-β `Search` into `/chess/*` + redeploy** — the web finally plays the 2105 engine.
5. **Lichess bot** (user go-live) — human-scale anchor + live corpus + the "watch it crush humans" payoff.
6. **Loop closure** — gauntlet/lichess PGN → `pgn` grammar → substrate (the flywheel).

**Phase C — gated on P's `ChessGraph` rethread landing.**
7. **Openings ingest + name capture** (`ChessOpenings` source) — the "teach it chess" signal + names.
8. **Eval overlays → Glicko-2 attestations** (self-calibrating, validated) — the fusion vision.
9. **Crazy-win analyzer** — learning signal + game review.

**Phase D — gated on decisions + bigger builds.**
10. **❓ Re-key decision → feature enrichment** — conventional metrics as learnable substructures.
11. **Content-addressed position cache + native thread-safety** → per-node substrate / A*.
12. **Data-driven eval** (learn weights from substrate). **(optional) raw strength climb.**

---

## 4. Decisions that are the user's

1. **Re-key for feature enrichment?** (one-time re-ingest to make conventional metrics learnable substructures) —
   the biggest fork; gates much of Phase D.
2. **Lichess go-live timing** (outward-facing, public games).
3. **Raw engine-strength climb** — pursue, or leave the engine at ~2105 and let the substrate be the differentiator?

---

## 5. Key technical notes / gotchas (so they don't bite again)

- `.cmd` files written by tooling default to **LF**; cmd.exe needs **CRLF** or it mis-tokenizes every line.
- **cutechess-cli needs `D:\Qt\6.8.3\msvc2022_64\bin` on PATH at runtime** (Qt DLLs); requires a `tc=` even with a
  depth cap (`tc=inf depth=N`).
- **`ChessCompose` native compose is NOT thread-safe** (AccessViolation under parallel) — serialize it (current
  stopgap: a static lock in `SubstrateRootBias`).
- **Position id = Merkle over ALL surface tokens** (placement + features), so any `PositionContent.Surface` change
  orphans the seed → the re-key decision (§4.1).
- Handicapped-by-depth Stockfish is a **bad** Elo anchor (positionally superhuman, tactically blind); use balanced
  `UCI_LimitStrength=true UCI_Elo=X`.
- The 24-game +211 was a hot sample; 500 games → +105±31. Always size the run to the claim.

Related memories: `chess-eval-overlays-attestation`, `chess-gauntlet-cutechess`, `chess-provenance-trust-model`,
`chess-openings-ingest`, `chess-time-clock-signals`, `laplace-iis-deploy`.
