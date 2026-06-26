## Bucket: A12_modality_chess — generic turn-based modality engine + chess

### Files read (all 30, in full)
- [x] app/Laplace.Chess.Service/ChessCompose.cs
- [x] app/Laplace.Chess.Service/ChessEngineService.cs
- [x] app/Laplace.Chess.Service/ChessGameFetcher.cs
- [x] app/Laplace.Chess.Service/ChessGraph.cs
- [x] app/Laplace.Chess.Service/ChessPgnDecomposer.cs
- [x] app/Laplace.Chess.Service/ChessVocabulary.cs
- [x] app/Laplace.Chess.Service/Laplace.Chess.Service.csproj
- [x] app/Laplace.Chess.Service/SubstrateTurnHost.cs
- [x] app/Laplace.Modality.Chess.Tests/AssemblyInfo.cs
- [x] app/Laplace.Modality.Chess.Tests/Laplace.Modality.Chess.Tests.csproj
- [x] app/Laplace.Modality.Chess.Tests/ModalityTests.cs
- [x] app/Laplace.Modality.Chess.Tests/PerftTests.cs
- [x] app/Laplace.Modality.Chess.Tests/PositionContentTests.cs
- [x] app/Laplace.Modality.Chess.Tests/SanTests.cs
- [x] app/Laplace.Modality.Chess.Tests/SelfPlayEngineTests.cs
- [x] app/Laplace.Modality.Chess/Bitboards.cs
- [x] app/Laplace.Modality.Chess/Board.cs
- [x] app/Laplace.Modality.Chess/ChessModality.cs
- [x] app/Laplace.Modality.Chess/ChessMove.cs
- [x] app/Laplace.Modality.Chess/ChessState.cs
- [x] app/Laplace.Modality.Chess/Laplace.Modality.Chess.csproj
- [x] app/Laplace.Modality.Chess/MoveApply.cs
- [x] app/Laplace.Modality.Chess/MoveGen.cs
- [x] app/Laplace.Modality.Chess/Perft.cs
- [x] app/Laplace.Modality.Chess/PositionContent.cs
- [x] app/Laplace.Modality.Chess/San.cs
- [x] app/Laplace.Modality/ITurnModality.cs
- [x] app/Laplace.Modality/Laplace.Modality.csproj
- [x] app/Laplace.Modality/ModalityEngine.cs
- [x] app/Laplace.Modality/TurnSubstrate.cs

---

### Headline conclusions (the questions the bucket asked)

**1. The known root-cause bug (UAX29 text-composer explosion) is FIXED in the live code path.**
`ChessCompose.cs` composes a position directly from its bounded substructure tokens: it splits the
canonical surface on spaces (`ChessCompose.Position`, lines 53-73), composes each token from its
codepoints (`ComposeToken`, lines 89-109, explicitly `bypasses UAX29 word-break`), then composes the
position as a tier-2 node over those tokens via the native merkle+centroid primitive
(`Hash128.Merkle` → native P/Invoke, verified in `Hash128.cs:39-50`). VERIFIED the wired path uses
this: `SubstrateTurnHost.Address` → `ChessCompose.PositionId` (line 44-45); `ChessGraph.EmitNodes`
→ `ChessCompose.Position` (line 77). `ContentEmitter` (the universal text composer) appears in the
chess service ONLY in doc comments, never in code (grep: `SubstrateTurnHost.cs:15`,
`ChessVocabulary.cs:11`). So domain content is NOT routed back through the text composer. **This is a
genuine fix, not a rename.** (See finding A12-6 for the now-stale docs that still describe the old broken path.)

**2. This is NOT secretly a normal chess engine.** Grep for `Stockfish|UciEngine|Process.Start|
alpha-beta|negamax|minimax|material|PieceValue` over the whole repo returns NO external engine and NO
material/search evaluator in the chess path (the only `negamax` hits are the consensus-value reflection
in `ModalityEngine.cs:132`, not a search). Move selection = (a) hard terminal ranks (forced
mate/loss/draw), (b) a depth-2 movegen-only mate-safety guard (`AllowsOpponentMate`,
`ModalityEngine.cs:145-152`), and (c) otherwise the **Glicko-2 consensus eff_mu** over MOVE edges
blended with a substructure OUTCOME fold (`SubstrateTurnHost.ValueStatesAsync`). There is no hand-tuned
positional evaluation. It is genuinely a consensus/Glicko-driven move chooser. CONFIDENCE high.

**3. Movegen is perft-verified by REAL tests (canonical values), but I did not execute them.**
`PerftTests.cs` asserts the standard published perft counts (startpos d6 = 119,060,324; Kiwipete
d5 = 193,690,690; pos3 d6 = 11,030,083; pos4 d5 = 15,833,292; pos5 d5 = 89,941,194; pos6 d4 =
3,894,594 — all canonical). `Perft.Run` (Perft.cs) drives the actual `MoveGen.Legal` + make/unmake, so
these are real correctness tests, not stubs. IF green, movegen + make/unmake are correct. I have NOT
run them this session, so I report the test design as real, not the run as passing.

**4. Tier is NOT used as a kind.** Kind lives in `type_id` (`PositionType`/`SubstructureType`,
`ChessGraph.AddNode` line 86); tier is the compositional-depth axis. No `EntityTier.Vocabulary`-style
violation here. (Minor: tier is stamped as a constant rather than computed — finding A12-8.)

**5. Identity = blake3(content), provenance in columns.** Position/substructure ids are
`Hash128.Merkle(tier, childIds)` — pure content, no source/name/order. `sourceId` is passed as the
entity-row column, not into the id (`AddNode`, line 86). Correct.

---

### Findings

**A12-1 — HIGH — invention-violation (provenance collapse).**
`ChessPgnDecomposer.SourceId => ChessVocabulary.SourceId` (ChessPgnDecomposer.cs:29), which is the
SAME id as self-play (`ChessVocabulary.SourceId = OfCanonical("substrate/source/ChessSelfPlay/v1")`,
ChessVocabulary.cs:18). So **real human/master PGN games and the bot's own self-play are attested under
one identical source id**, with `SourceName = "ChessSelfPlay"` (a misnomer for PGN ingest). Per-game
and per-player provenance is lost: every attestation passes `contextId: null`
(`ChessGraph.AppendMoveEdge` line 70, `Outcome` line 110), player names are never recorded, and clock
data is explicitly skipped (`ChessPgnDecomposer.cs:24-25` comment: "Clock/criticality weighting ...
not done here"). The design (memory `chess-substrate-design`: "per-game attestations
(player/Elo/clock as provenance)") is therefore only partly realised. The consensus denoiser's whole
premise is source-weighted trust; collapsing two very different trust sources into one source id
undermines it. PARTIAL mitigation: opponent Elo is encoded as the Glicko game-count
(`EloGames`, 1..12, ChessPgnDecomposer.cs:141-142) vs self-play's `games:1`
(SubstrateTurnHost.cs:171), so master plies outweigh self-play plies ~12:1 by count — but
trust-by-source and player identity are still gone. VERIFIED by tracing `SourceId`/`contextId` at
every `AddAttestation`/`AddEntity` call. CONFIDENCE high.

**A12-2 — MEDIUM — fake-test / coverage gap (the novel part is untested).**
The only test that touches the engine end-to-end (`SelfPlayEngineTests.cs`) replaces BOTH substrate
seams with in-memory stubs: `FnvAddresser` (FNV-1a instead of the real composed Merkle id, lines
20-33) and `FakeSubstrate` (a dictionary with a hand-shaped eff_mu, lines 38-78). So the genuinely
novel/risky code — `ChessCompose` native composition, `ChessGraph` attestation emission,
`SubstrateTurnHost.ValueStatesAsync`/`EffMuAsync`, the Glicko fold — has **zero automated coverage**;
it is honest about being a stub ("WITHOUT the native substrate"), but that leaves the consensus-engine
integration verified only by manual runs. Pure-chess code (perft/SAN/terminal/surface) is well tested.
The shrinkage constant `ShrinkK0 = 15000` (SubstrateTurnHost.cs:79) and the whole `ValueStatesAsync`
weighting are justified by inline "Measured:" comments with no committed test/benchmark. CONFIDENCE high.

**A12-3 — MEDIUM — correctness / silent data loss (acknowledged).**
`ChessPgnDecomposer.AppendGame` aborts the **rest of a game** on the first unresolved SAN token:
`if (mv is null) return;` (line 128). Any `San.Resolve` gap (it returns null on ambiguous/malformed,
San.cs:13) silently truncates that game's remaining plies — no log, no counter. `San.Resolve` is a
hand-rolled SAN parser (no SAN generation cross-check), so a resolver gap drops real training data
quietly. The PGN result tag `*` is also silently dropped (`ParseResult` line 199 → null → game
skipped). Acknowledged in the comment but still a silent partial-ingest path. CONFIDENCE high.

**A12-4 — MEDIUM — dead/diagnostic code left in the write path (silent corruption risk).**
`ChessGraph.AddNode` honours two env toggles in the production write path:
`LAPLACE_CHESS_NOPHYS=1` skips the physicality row entirely (line 87) and `LAPLACE_CHESS_NOTRAJ=1`
writes an empty trajectory + `NConstituents:0` (lines 88, 96-97). Both are labelled "DIAGNOSTIC
bisection". If either is set in an environment, ingest silently drops the S³ geometry / lossless
constituent trajectory — corrupting the substrate with no error. Their existence is also direct
evidence of an unresolved native crash being bisected (consistent with the `chess-substrate-design`
memory's "AccessViolation crash" and the `ChessCompose.cs:34` mention of "the native heap race", and
with `ConsensusAccumulatingWriter` being run with `foldWorkers: 1` in ChessEngineService.cs:96 — a
concurrency throttle). The native AV root cause is outside this bucket (Engine.Core/native libs), but
this bucket carries the diagnostic scaffolding. CONFIDENCE high (toggles), med (AV linkage).

**A12-5 — LOW — invention-tension (§6 invented `substrate/type/X/v1` namespace).**
`ChessVocabulary` mints `PositionType/SubstructureType/MoveType/OutcomeType/OutcomeObject` via
`Hash128.OfCanonical("substrate/type/Chess_Position/v1")` etc. (lines 18-36) — exactly the
`substrate/type/X/v1` namespace charter §6 calls out as the anti-pattern. Unlike linguistic concepts,
chess has no external registry (no ILI/UPOS analogue), so there is no "real external id" to anchor on;
minting is arguably unavoidable. Flagging as the rule-text violation with the mitigating context. The
type-symbol-by-name approach is consistent with the rest of the substrate. CONFIDENCE high (it is the
named anti-pattern), low (that it's actually wrong here).

**A12-6 — LOW — disparagement/wrong-prose (stale docs describing the OLD broken path).**
Multiple doc comments still describe the pre-fix architecture that routes content through the universal
text composer / `ContentEmitter`, contradicting the actual code (finding-headline #1):
- `PositionContent.cs:7-11` — "The universal substrate composer segments it into words → the position
  entity is the Merkle/centroid composition of these substructure words". The live path
  (`ChessCompose`) does NOT use the universal composer.
- `SubstrateTurnHost.cs:14-16, 41-43` — "composes ... to its content id (`ContentEmitter.RootId`)";
  code actually calls `ChessCompose.PositionId`.
- `TurnSubstrate.cs:8-11, 19-27` — `IContentAddresser`/`RecordedEdge` docs say "the host implements
  this with `ContentEmitter.RootId` ... the codepoint→…→ tree"; the chess host does not.
- `ChessVocabulary.cs:9-12` — position instances "routed through `ContentEmitter`".
Per charter, stale/wrong prose is itself a defect; report alongside the (correct) code. CONFIDENCE high.

**A12-7 — LOW — perf footgun (fetcher reads whole PGN into RAM).**
`ChessGameFetcher.FetchLichessAsync` streams the download to disk (good) but then
`CountGames(await File.ReadAllTextAsync(outPath, ct))` (line 123) loads the ENTIRE written PGN back
into a single string just to count `[Event ` tags — O(file) RAM, contradicting the streaming intent
for large dumps. `FetchChessComAsync` is fine (per-archive). CONFIDENCE high.

**A12-8 — INFO — tier stamped, not computed.**
`ChessCompose` hardcodes `SubstructureTier = 1`, `PositionTier = 2` (lines 44-45) and passes them to
`Hash128.Merkle(tier, ...)` / `AddEntity(n.Id, n.Tier, ...)` rather than deriving `tier = max(child)+1`.
The values are correct by construction (codepoint=0 → token=1 → position=2), and tier is content-derived
(depth) so baking it into the Merkle id is sound — but it is stamped, not emergent in the §3 sense.
CONFIDENCE high.

**A12-9 — INFO — selection cost is O(branching²) movegen per ply.**
`ModalityEngine.ScoreMovesAsync` calls `Terminal(next)` (which itself runs `MoveGen.Legal`) and
`AllowsOpponentMate(next)` (a full legal-move loop with `Apply`+`Terminal` per opponent reply) for
every candidate (lines 104-125, 145-152), plus a native composition of each successor surface in
`ValueStatesAsync`. ~35² ≈ 1.2k movegens + 35 native compositions + 1 DB round-trip per ply. Bounded
and deterministic (the depth-2 mate guard is deliberate), but heavy. Not a defect, noted for perf. CONFIDENCE high.

**A12-10 — INFO (clean) — the fold is online, NOT the drain anti-pattern.**
`SubstrateTurnHost.LearnGameAsync` does `ApplyAsync` then `FoldIncrementalAsync` (lines 173-175). I
traced `ConsensusAccumulatingWriter.FoldIncrementalAsync` (lines 643-659): it flushes the current
period and awaits the per-partition `materialize_period_partition` fold so the touched edges are
updated in place ("the immediate, no-drain update"), and it *throws* if put in the terminal/bulk/walk
lanes. This is the per-game online fold, not a whole-DB "catch-up" drain, so it does NOT violate the
§3 inline-fold invariant. CONFIDENCE high.

---

### Bucket summary
- CRITICAL: 0
- HIGH: 1 (A12-1 provenance collapse — PGN + self-play share one source id; player/clock/context dropped)
- MEDIUM: 3 (A12-2 untested substrate integration; A12-3 silent game truncation on SAN miss; A12-4 diagnostic env toggles that silently drop geometry)
- LOW: 3 (A12-5 invented type namespace; A12-6 stale docs describing the fixed-out broken path; A12-7 fetcher RAM)
- INFO: 3 (A12-8 stamped tier; A12-9 O(b²) selection; A12-10 fold is correctly online)

**Single worst issue:** A12-1 — real PGN (master) games and the bot's self-play are attested under one
identical source id with no per-game/player/clock provenance and `contextId: null` throughout. This
defeats source-weighted trust, the core mechanism of the consensus denoiser, and leaves the
"player-analysis" provenance vision unimplemented; only opponent-Elo-as-game-count partially survives.

**Reassuring conclusions:** the headline root-cause bug (UAX29 surface explosion) is genuinely fixed in
the wired path; this is a real consensus/Glicko engine, not a wrapper around a conventional engine; and
movegen is gated by real canonical perft tests (not run by me this session).
