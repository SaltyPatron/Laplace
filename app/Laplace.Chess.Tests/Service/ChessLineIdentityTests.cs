using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// GH #736 — the identity law itself. The game CONTENT entity is the LINE:
/// Hash128.Merkle(LineTier, start-position id + ordered typed move ids), minted at extract time by replay, so
/// identical PLAY collides regardless of who played it, when, or how the source spelled
/// the SAN. The PLAYING is a provenance handle: distinct per record, used as
/// attestation context and as the subject of exactly one record edge,
/// (playing, PLAYS_LINE, line).
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
    public void SameMainline_DifferentNotationAndProvenance_SameLine_DifferentPlayings()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        var b = ChessPgnDecomposer.TryParseGame(GameVariantNotation)!;

        Assert.Equal(a.LineId, b.LineId);       // one PLAY, one content entity
        Assert.NotEqual(a.PlayingId, b.PlayingId);  // two playings, two provenance handles
        Assert.NotEqual(a.EventId, b.EventId);      // different [Event] tags → different tournaments
    }

    [Fact]
    public void SameTournamentTags_ShareEventId_DistinctPlayingsAndLines()
    {
        const string g1 =
            "[Event \"Open\"]\n[Site \"Oslo\"]\n[Date \"2025.01.01\"]\n"
            + "[White \"A\"]\n[Black \"B\"]\n[Round \"1\"]\n[Result \"1-0\"]\n\n1. e4 e5 1-0\n";
        const string g2 =
            "[Event \"Open\"]\n[Site \"Oslo\"]\n[Date \"2025.01.01\"]\n"
            + "[White \"C\"]\n[Black \"D\"]\n[Round \"1\"]\n[Result \"0-1\"]\n\n1. d4 d5 0-1\n";
        var a = ChessPgnDecomposer.TryParseGame(g1)!;
        var b = ChessPgnDecomposer.TryParseGame(g2)!;

        Assert.Equal(a.EventId, b.EventId); // one tournament, many games
        Assert.NotEqual(a.PlayingId, b.PlayingId);
        Assert.NotEqual(a.LineId, b.LineId);
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
    public void LineId_IsTheMerkleOfStartPositionAndOrderedMoves()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        Assert.Equal(parsed.Moves.Count + 1, parsed.PositionIds.Length); // start included
        Assert.Equal(parsed.Moves.Count, parsed.MoveIds.Length);
        Assert.Equal(ChessCompose.LineId(parsed.PositionIds[0], parsed.MoveIds), parsed.LineId);
    }

    // Cross-lane collision, the book path: a prose line replayed through TryReplayLine
    // must land on the same line entity a PGN playing of those moves mints.
    [Fact]
    public void ProseLineReplay_CollidesWithThePgnLine()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        var replayed = ChessPgnDecomposer.TryReplayLineDetailed(
            ["e4", "e5", "Nf3", "Nc6", "Bc4", "Nf6", "O-O", "Bc5"], startFen: null);
        Assert.NotNull(replayed);
        Assert.Equal(parsed.LineId,
            ChessCompose.LineId(replayed!.PositionIds[0], replayed.MoveIds));
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
        var startPositionId = ChessCompose.PositionId(state.Board);
        var moveIds = new List<Hash128>();
        foreach (var san in parsed.Moves)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), san);
            Assert.NotNull(mv);
            moveIds.Add(ChessCompose.MoveId(state.Board.Squares[mv!.Value.From], mv.Value));
            state = m.Apply(state, mv!.Value);
        }

        Assert.Equal(parsed.LineId,
            ChessCompose.LineId(startPositionId,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(moveIds)));
    }

    // The recorder's emission shape: exactly one structural (playing, PLAYS_LINE, line)
    // edge. The result is witnessed separately at playing grain; neither the join nor the
    // reusable line is an outcome-bearing move fact.
    [Fact]
    public void Recorder_EmitsStructuralPlaysLine_WithoutLineOutcomeProjection()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/identity");
        ChessPgnDecomposer.RecordGame(parsed, b);
        var change = b.SetInputUnitsConsumed(1).Build();

        var plays = Assert.Single(change.Attestations, a => a.TypeId == ChessVocabulary.PlaysLineType);
        Assert.Equal(parsed.PlayingId, plays.SubjectId);
        Assert.Equal(parsed.LineId, plays.ObjectId);
        Assert.Equal(1, plays.ObservationCount);

        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == ChessVocabulary.OutcomeType && a.SubjectId == parsed.LineId);
        Assert.Contains(change.Attestations,
            a => a.TypeId == ChessVocabulary.HasResultType
                 && a.SubjectId == parsed.LineId
                 && a.ContextId == parsed.PlayingId);

        // Line = content; playing = novelty/attestation handle; tournament Event is separate.
        Assert.Contains(change.Entities, e => e.Id == parsed.LineId && e.TypeId == ChessVocabulary.GameType);
        Assert.Contains(change.Entities, e => e.Id == parsed.PlayingId && e.TypeId == ChessVocabulary.PlayingType);
        Assert.Contains(change.Entities, e => e.Id == parsed.EventId && e.TypeId == ChessVocabulary.EventType);
    }

    [Fact]
    public void SameLine_DifferentPlayingResults_AreRecoveredFromPlayingContext()
    {
        var whiteWin = ChessPgnDecomposer.TryParseGame(GameCanonical)!;
        var blackWinPgn = GameCanonical
            .Replace("[Result \"1-0\"]", "[Result \"0-1\"]", StringComparison.Ordinal)
            .Replace("Bc5 1-0", "Bc5 0-1", StringComparison.Ordinal);
        var blackWin = ChessPgnDecomposer.TryParseGame(blackWinPgn)!;
        Assert.Equal(whiteWin.LineId, blackWin.LineId);
        Assert.NotEqual(whiteWin.PlayingId, blackWin.PlayingId);

        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/results");
        ChessPgnDecomposer.RecordGame(whiteWin, b);
        ChessPgnDecomposer.RecordGame(blackWin, b);
        var change = b.SetInputUnitsConsumed(2).Build();

        Assert.Equal(2, change.Attestations.Count(a =>
            a.TypeId == ChessVocabulary.PlaysLineType
            && a.ObjectId == whiteWin.LineId));
        Assert.Equal(2, change.Attestations.Count(a =>
            a.TypeId == ChessVocabulary.HasResultType
            && a.SubjectId == whiteWin.LineId
            && (a.ContextId == whiteWin.PlayingId || a.ContextId == blackWin.PlayingId)));
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == ChessVocabulary.OutcomeType && a.SubjectId == whiteWin.LineId);
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
