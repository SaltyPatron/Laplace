using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// GH #736 — the identity law itself. The game CONTENT entity is the LINE:
/// Hash128.Merkle(LineTier, ordered position ids), minted at extract time by replay, so
/// identical PLAY collides regardless of who played it, when, or how the source spelled
/// the SAN. The playing EVENT is a provenance handle: distinct per record, used as
/// attestation context and as the subject of exactly one record edge,
/// (event, PLAYS_LINE, line).
/// </summary>
public sealed class ChessLineIdentityTests
{
    // One mainline, written two ways: different players/date/round, "O-O" vs "0-0",
    // "!?"-suffixed SAN, and an explicit disambiguation ("Ngf3" for the only knight
    // that can reach f3). Writer's-choice notation must never mint a second line.
    private const string GameCanonical =
        "[Event \"A\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Nf3 Nc6 3. Bc4 Nf6 4. O-O Bc5 1-0\n";
    private const string GameVariantNotation =
        "[Event \"B\"]\n[White \"Carol\"]\n[Black \"Dave\"]\n[Date \"2025.06.30\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Ngf3!? Nc6 3. Bc4 Nf6 4. 0-0 Bc5 1-0\n";

    // Queen's Gambit Declined move orders: the same FINAL position via different
    // intermediate positions. Content identity is the path, not the destination.
    private const string GameQgdDirect =
        "[Event \"C\"]\n[White \"E\"]\n[Black \"F\"]\n[Date \"2024.01.01\"]\n[Result \"1/2-1/2\"]\n\n"
        + "1. d4 d5 2. c4 e6 1/2-1/2\n";
    private const string GameQgdTransposed =
        "[Event \"D\"]\n[White \"E\"]\n[Black \"F\"]\n[Date \"2024.01.01\"]\n[Result \"1/2-1/2\"]\n\n"
        + "1. c4 e6 2. d4 d5 1/2-1/2\n";

    [Fact]
    public void SameMainline_DifferentNotationAndProvenance_SameLine_DifferentEvents()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        var b = ChessPgnDecomposer.TryParseGame(GameVariantNotation)!;

        Assert.Equal(a.LineId, b.LineId);       // one PLAY, one content entity
        Assert.NotEqual(a.EventId, b.EventId);  // two playings, two provenance handles
    }

    [Fact]
    public void Transposition_SameFinalPosition_DifferentLine()
    {
        var direct = ChessPgnDecomposer.TryParseGame(GameQgdDirect)!;
        var transposed = ChessPgnDecomposer.TryParseGame(GameQgdTransposed)!;

        // The final positions collide (that is what a transposition IS) …
        Assert.Equal(direct.PositionIds[^1], transposed.PositionIds[^1]);
        // … but the paths differ, so the lines differ.
        Assert.NotEqual(direct.LineId, transposed.LineId);
    }

    [Fact]
    public void LineId_IsTheMerkleOfTheOrderedPositionIds()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        Assert.Equal(parsed.Moves.Count + 1, parsed.PositionIds.Length); // start included
        Assert.Equal(ChessCompose.LineId(parsed.PositionIds), parsed.LineId);
    }

    // Cross-lane collision, the book path: a prose line replayed through TryReplayLine
    // must land on the same line entity a PGN playing of those moves mints.
    [Fact]
    public void ProseLineReplay_CollidesWithThePgnLine()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        var replayed = ChessPgnDecomposer.TryReplayLine(
            ["e4", "e5", "Nf3", "Nc6", "Bc4", "Nf6", "O-O", "Bc5"], startFen: null);
        Assert.NotNull(replayed);
        Assert.Equal(parsed.LineId, ChessCompose.LineId(replayed!));
    }

    // Cross-lane collision, the live path: the live host accumulates
    // ChessCompose.PositionId(stateKey) per ply and mints the line at completion. The
    // same moves played live must collide with the PGN-extracted line.
    [Fact]
    public void LiveStateKeySequence_CollidesWithThePgnLine()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(GameCanonical)!;

        var m = new ChessModality();
        var state = m.Initial();
        var positionIds = new List<Hash128> { ChessCompose.PositionId(m.StateKey(state)) };
        foreach (var san in parsed.Moves)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), san);
            Assert.NotNull(mv);
            state = m.Apply(state, mv!.Value);
            positionIds.Add(ChessCompose.PositionId(m.StateKey(state)));
        }

        Assert.Equal(parsed.LineId,
            ChessCompose.LineId(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positionIds)));
    }

    // The recorder's emission shape: exactly one (event, PLAYS_LINE, line) edge carrying
    // the playing's white-POV outcome, and the line's own (line, OUTCOME) fold cell with
    // ctx = the event.
    [Fact]
    public void Recorder_EmitsPlaysLine_AndLineOutcome_PerPlaying()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/identity");
        ChessPgnDecomposer.RecordGame(parsed, b);
        var change = b.SetInputUnitsConsumed(1).Build();

        var plays = Assert.Single(change.Attestations, a => a.TypeId == ChessVocabulary.PlaysLineType);
        Assert.Equal(parsed.PlayingId, plays.SubjectId);
        Assert.Equal(parsed.LineId, plays.ObjectId);
        Assert.Equal(Glicko2.ScoreWin, plays.SumScoreFp1e9); // 1-0, white POV
        Assert.Equal(1, plays.ObservationCount);

        var lineOutcome = Assert.Single(change.Attestations,
            a => a.TypeId == ChessVocabulary.OutcomeType && a.SubjectId == parsed.LineId);
        Assert.Equal(parsed.PlayingId, lineOutcome.ContextId);
        Assert.Equal(Glicko2.ScoreWin, lineOutcome.SumScoreFp1e9);
        Assert.Equal(1, lineOutcome.ObservationCount);

        // Both entities exist for the novelty gate: the line as content, the event as
        // the slim provenance handle.
        Assert.Contains(change.Entities, e => e.Id == parsed.LineId && e.TypeId == ChessVocabulary.GameType);
        Assert.Contains(change.Entities, e => e.Id == parsed.EventId && e.TypeId == ChessVocabulary.EventType);
    }

    // Idempotent re-ingest: once a record's event is present, a second pass over the
    // same file yields nothing — while the SAME LINE arriving under a NEW event (a
    // different playing) still flows.
    [Fact]
    public async Task Reingest_SameRecordSkips_NewPlayingOfSameLineFlows()
    {
        var first = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        var second = ChessPgnDecomposer.TryParseGame(GameVariantNotation)!; // same line, new event
        var reader = new FakeReader();
        reader.Present.Add(first.PlayingId);

        var novel = new List<ChessGameRecord>();
        await foreach (var g in ChessPgnDecomposer.FilterNovelAsync(
            [first, second], reader, CancellationToken.None))
            novel.Add(g);

        var kept = Assert.Single(novel);
        Assert.Equal(second.PlayingId, kept.PlayingId);
        Assert.Equal(first.LineId, kept.LineId); // the shared line did not block the new playing
    }

    private sealed class FakeReader : ISubstrateReader
    {
        public readonly HashSet<Hash128> Present = new();
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default) => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            var bm = new byte[(candidates.Count + 7) / 8];
            for (int i = 0; i < candidates.Count; i++)
                if (Present.Contains(candidates[i])) bm[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bm);
        }
    }
}
