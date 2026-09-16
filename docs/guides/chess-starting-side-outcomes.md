# Chess starting-side outcome correction

The move-outcome and position-transition projections must score each ply from the
side that actually moved. A PGN with `SetUp "1"` can start with Black. The ordinal
alone does not identify its mover.

The parser retains the initial side from its existing legal native board replay.
That field is admission evidence; it is not a constituent, an identity salt or a
new persisted entity. Hydrated games recover the side from their admitted initial
board. Live completion uses its first native-resolved ply, and both batch live
result-inference owners inspect the actual pre-move board. Subsequent standard
chess plies alternate from that starting side.

The correction preserves the position, move, line and playing recipes, attestation
identities, source/context bindings, move order and observation multiplicity.
White-start scores remain the same. Processing timestamps from separate executions
are not expected to be byte-identical.

## Acceptance

`ChessStartingSideOutcomeTests` exercises the actual PGN grammar, legal native
replay, recorder, hydrated projection entry points, native move resolution and the
two live result-inference methods. Paired complete Fool's Mate games start either
from the normal White-to-move board or from the validated Black-to-move FEN after
White's f3. A second pair repeats Black knight move objects while White pawn
advances prevent position repetition, then reaches the same genuine checkmate.

The controls require every ply to be legal, the terminal board to be checkmate
with the declared Black winner, parsed/hydrated transition parity, exact
board-relative scores and ordinary native attestation fields, unchanged canonical
ids and v1 marker ids, and summed observation counts equal to the full move count.
The repeated-move pair checks aggregation without losing occurrences. The live
controls execute the native resolved-move and outcome-inference boundaries plus
their shared projection functions; they do not simulate a PostgreSQL commit or
claim a remote Lichess game was played. An absent parsed admission side is refused
before either the recorder or transition projection stages entities, attestations,
physicalities or observations. Empty-game recording remains compatible.

Run this class together with existing recorder, move-outcome, line-identity,
fused-ingest and live-game tests through the normal managed test owner. Source
review is not an execution result.

## Historical testimony and completion markers

Both projection markers intentionally remain v1. Attestation ids do not contain
the score. Changing only the marker version would make previously observed games
eligible for another deposit while their old evidence remained, and changing the
formula alone does not retroactively replace rows already marked complete.
Replaying the same intent can also be correctly suppressed by the common replay
journal. No automatic historical data repair is claimed by this source change.

The existing retraction owner is
`NpgsqlSubstrateReader.EvictSourceAsync` → `ops.evict_source`. It removes the
selected source's evidence, refolds affected consensus cells from surviving
replayable evidence, removes owned completion markers/file checkpoints/replay
claims and repairs masks. It preserves shared content entities. Its guard rejects
a refold involving non-replayable calibration evidence.

Actual ownership differs by entry path:

| Projection | Existing owner | Historical recovery scope |
| --- | --- | --- |
| PGN/hydrated/live-host position transitions | `ChessTransitions`, playing context | Existing `laplace evict ChessTransitions --rederive` retracts this isolated calculated source and uses `chess-transitions` to replay retained PGN/live games. The CLI knows this source's marker type. |
| Standalone move backfill | `ChessMoveOutcomes`, null context | Source-only eviction with explicit `Chess_AnalysisMarker` cleanup, followed by the existing `chess-move-outcomes` source, is the corresponding owner boundary. This source is not in the CLI's `--rederive` map; do not claim that combined shortcut supports it. |
| Inline PGN move outcomes | `ChessPgn` (or the explicit recording source), null context | Shares its source and OUTCOME relation with other recorded facts, including player results. It requires an exact projection-scoped replacement; blanket source or relation eviction is not a safe targeted repair. |
| Live move outcomes and direct `SubstrateTurnHost` transitions | `ChessSelfPlay`, null/playing context respectively | Shares a recorded source with live metadata, results and player facts. An exact projection-scoped replacement and complete contributor inventory are required. |

A live game recorded through a batch owner may also have an already-inverted
inferred game result. Correcting its projections from that stored result alone
would preserve the wrong premise. Recovery must compare retained move sequence
and terminal native board, and retain original external result provenance. A
forced mate can establish the winner; an ongoing terminal source occurrence
requires its actual resignation/time/agreement evidence. Do not invent a winner
for an ambiguous historical observation.

Before any retraction, produce a bounded inventory using the existing
`FetchRecordedPlayingIdPageAsync` continuation cursor with `includeLive: true`
and `HydratePositionOutcomeInputsAsync` under an explicit materialization byte
grant. Record the playing/line/start ids, source ownership, exact initial side,
result, ply count and existing projection evidence/markers. Recover the board
through the same native owner and verify its start id. Classify missing or
unreadable source evidence explicitly; do not assume a missing FEN means a
validated standard board. Retain the inventory and selected existing evidence,
and prevent concurrent writes to the affected source during replacement.

For isolated `ChessTransitions`, use its existing complete-source retraction and
re-derivation only after the inventory establishes that all required witnessed
inputs survive. Verify unchanged game/content ids and source observations outside
that lane, correct per-playing transition scores and occurrence totals, and a
second replay that adds no evidence. The operation re-derives White-start games as
well; their identities and outcome totals must match the retained pre-repair
inventory. Refold timestamps/ratings need the existing canonical refold contract,
not an unsupported bitwise equality claim for historical incremental periods.

For null-context move cells, a Black-start contribution has already been merged
with other games under the same source. An affected-game list cannot safely
subtract it or rebuild only that subset. A replacement must enumerate every
contributor to the affected cells, including White-start games, and replay their
full multiplicities through the common evidence/fold owner. The currently
inspected eviction API scopes by source/relation/marker type, not exact
move-subject cells; this change does not add that missing selective durable
replacement API. Do not use a direct SQL score patch, an analyzer version bump or
forced full PGN reingest as a substitute. The last option would re-observe
unrelated recorded facts.

## Corpus selection coverage

`ChessCorpusPreparation.PrepareAsync` selects complete legal source games with
`TryParseGame(..., requireCompleteSource: true)`. It does not exclude SetUp/FEN
games or Black-to-move starts. Its selection manifest retains each
`StartPositionId`, so the exact selected source can establish coverage.

The previous 2,000-game pilot cohort is now verified to start with White. The
[independent host observation](https://github.com/SaltyPatron/Laplace/actions/runs/35091710818/job/104779326909)
read the complete 256,162,073-byte source and verified SHA256
`9eafc83ed9a97f9dcfc82135448d45653d325a742e497e8d08c3085a6a48b0ea`.
Its first selected frame was 763 bytes with SHA256
`474bc75575390aa71474bd618e8b26beeffac1f3f78a96bcf9695039b23c24ac`,
matching the retained native-prepared frame, and contained neither SetUp nor FEN.
The unchanged parser therefore selected the standard White-to-move initial board.
All 2,000 retained selection entries had that same starting identity,
`6e209f78d66f64f1405d9184f65065bb`. This establishes that the prior pilot did
not exercise the Black-start defect; it is not a throughput or admission result.

Future selections must establish their own starting-position coverage from their
bound manifest and source. A distinct-start count by itself is insufficient.
The paired native fixtures provide targeted executable coverage separately from
the pilot and from any throughput claim.
