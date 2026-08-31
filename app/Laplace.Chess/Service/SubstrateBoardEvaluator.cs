using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Immutable, bounded snapshot of substrate outcome consensus for chess position atoms.
/// Search calls <see cref="Evaluate"/> at every leaf without issuing database queries.
/// </summary>
public sealed class SubstrateBoardEvaluator : ISearchPositionEvaluator
{
    private readonly AtomValue[] _side = new AtomValue[2];
    private readonly AtomValue[] _castling = new AtomValue[16];
    private readonly AtomValue[] _enPassant = new AtomValue[65];
    private readonly AtomValue[] _pieceSquare = new AtomValue[12 * 64];
    private readonly double _cpPerPoint;
    private readonly int _capCp;
    private long _positionReads;
    private long _positionsWithEvidence;

    private readonly record struct AtomValue(double EffMu, double Rd, double Witnesses, bool Present);

    public SubstrateBoardEvaluator(NpgsqlDataSource ds, double cpPerPoint = 8d, int capCp = 200)
    {
        ArgumentNullException.ThrowIfNull(ds);
        _cpPerPoint = cpPerPoint;
        _capCp = Math.Max(0, capCp);

        var slots = AtomUniverse();
        var edgeIds = slots.Select(static slot => ConsensusKeys.EdgeId(
            slot.Id, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject)).ToArray();
        var rows = NpgsqlConsensusByIds.Read(ds, edgeIds, ChessVocabulary.OutcomeType);
        for (int i = 0; i < slots.Count; i++)
        {
            if (!rows.TryGetValue(edgeIds[i], out var row)) continue;
            Set(slots[i], new AtomValue(row.EffMu, row.Rd, row.Witnesses, true));
            LoadedAtoms++;
        }
    }

    internal SubstrateBoardEvaluator(
        IReadOnlyDictionary<Hash128, (double EffMu, double Rd, double Witnesses)> values,
        double cpPerPoint = 8d, int capCp = 200)
    {
        _cpPerPoint = cpPerPoint;
        _capCp = Math.Max(0, capCp);
        foreach (var slot in AtomUniverse())
        {
            if (!values.TryGetValue(slot.Id, out var value)) continue;
            Set(slot, new AtomValue(value.EffMu, value.Rd, value.Witnesses, true));
            LoadedAtoms++;
        }
    }

    public int Evaluate(Board board)
    {
        Interlocked.Increment(ref _positionReads);
        double sum = 0d, weightSum = 0d;

        Add(_side[board.WhiteToMove ? 1 : 0], ref sum, ref weightSum);
        Add(_castling[ChessPositionIdentity.CastlingDestinationMask(board)], ref sum, ref weightSum);
        int ep = ChessModality.CapturableEpSquare(board);
        Add(_enPassant[ep < 0 ? 64 : (Board.RankOf(ep) << 3) | Board.FileOf(ep)], ref sum, ref weightSum);

        for (int square = 0; square < 128; square++)
        {
            if ((square & 0x88) != 0) { square += 7; continue; }
            Piece piece = board.Squares[square];
            if (piece == Piece.Empty) continue;
            int bit = (Board.RankOf(square) << 3) | Board.FileOf(square);
            Add(_pieceSquare[ChessPositionIdentity.PieceOrdinal(piece) * 64 + bit],
                ref sum, ref weightSum);
        }
        if (weightSum == 0d) return 0;
        Interlocked.Increment(ref _positionsWithEvidence);

        // Stored constituent outcomes use White's fixed POV. Negamax needs side-to-move POV.
        double whitePoints = sum / weightSum / 1e9;
        double stmPoints = board.WhiteToMove ? whitePoints : -whitePoints;
        return Math.Clamp((int)Math.Round(stmPoints * _cpPerPoint), -_capCp, _capCp);
    }

    public int LoadedAtoms { get; private set; }
    public long PositionReads => Volatile.Read(ref _positionReads);
    public long PositionsWithEvidence => Volatile.Read(ref _positionsWithEvidence);

    private static void Add(AtomValue value, ref double sum, ref double weightSum)
    {
        if (!value.Present) return;
        double confidence = GlickoPriors.InitialRd /
                            (GlickoPriors.InitialRd + Math.Max(0d, value.Rd));
        double weight = Math.Sqrt(Math.Max(1d, value.Witnesses)) * confidence;
        sum += (value.EffMu - GlickoPriors.NeutralMu) * weight;
        weightSum += weight;
    }

    private enum AtomKind : byte { Side, Castling, EnPassant, PieceSquare }
    private readonly record struct AtomSlot(Hash128 Id, AtomKind Kind, int Index);

    private void Set(AtomSlot slot, AtomValue value)
    {
        switch (slot.Kind)
        {
            case AtomKind.Side: _side[slot.Index] = value; break;
            case AtomKind.Castling: _castling[slot.Index] = value; break;
            case AtomKind.EnPassant: _enPassant[slot.Index] = value; break;
            case AtomKind.PieceSquare: _pieceSquare[slot.Index] = value; break;
        }
    }

    private static IReadOnlyList<AtomSlot> AtomUniverse()
    {
        var slots = new List<AtomSlot>(2 + 16 + 65 + 12 * 64);
        lock (ChessCompose.Gate)
        {
            for (ushort side = 0; side <= 1; side++)
                slots.Add(new AtomSlot(ChessPositionIdentity.AtomId(
                    ChessPositionIdentity.Atom.Scalar(ChessPositionIdentity.SideDomain, side)),
                    AtomKind.Side, side));
            for (ushort castling = 0; castling < 16; castling++)
                slots.Add(new AtomSlot(ChessPositionIdentity.AtomId(
                    ChessPositionIdentity.Atom.Scalar(ChessPositionIdentity.CastlingDomain, castling)),
                    AtomKind.Castling, castling));
            for (ushort ep = 0; ep <= 64; ep++)
                slots.Add(new AtomSlot(ChessPositionIdentity.AtomId(
                    ChessPositionIdentity.Atom.Scalar(ChessPositionIdentity.EnPassantDomain, ep)),
                    AtomKind.EnPassant, ep));
            for (int piece = 0; piece < 12; piece++)
            for (int square = 0; square < 64; square++)
                slots.Add(new AtomSlot(ChessPositionIdentity.AtomId(
                    ChessPositionIdentity.Atom.Scalar(
                        ChessPositionIdentity.PieceSquareDomain, checked((ushort)((piece << 6) | square)))),
                    AtomKind.PieceSquare, piece * 64 + square));
        }
        return slots;
    }
}
