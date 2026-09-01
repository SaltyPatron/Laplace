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
    private sealed class Snapshot
    {
        public readonly AtomValue[] Side = new AtomValue[2];
        public readonly AtomValue[] Castling = new AtomValue[16];
        public readonly AtomValue[] EnPassant = new AtomValue[65];
        public readonly AtomValue[] PieceSquare = new AtomValue[12 * 64];
        public int LoadedAtoms;
        public long Generation;
    }

    private sealed class SearchSnapshot(
        SubstrateBoardEvaluator owner, Snapshot snapshot, long version) : ISearchPositionEvaluator
    {
        public long Version => version;
        public int Evaluate(Board board) => owner.Evaluate(board, snapshot);
    }

    private readonly NpgsqlDataSource? _ds;
    private readonly Func<IReadOnlyDictionary<Hash128, (double EffMu, double Rd, double Witnesses)>>? _loadValues;
    private readonly Func<long> _epoch;
    private readonly object _refreshGate = new();
    private readonly double _cpPerPoint;
    private readonly int _capCp;
    private Snapshot _snapshot;
    private long _observedEpoch;
    private long _positionReads;
    private long _positionsWithEvidence;

    private readonly record struct AtomValue(double EffMu, double Rd, double Witnesses, bool Present);

    public SubstrateBoardEvaluator(NpgsqlDataSource ds, double cpPerPoint = 8d, int capCp = 200)
    {
        ArgumentNullException.ThrowIfNull(ds);
        _ds = ds;
        _epoch = static () => ChessTransitionObservations.Epoch;
        _cpPerPoint = cpPerPoint;
        _capCp = Math.Max(0, capCp);
        _snapshot = ReadSnapshot(ds);
        _snapshot.Generation = 1;
        _observedEpoch = _epoch();
    }

    internal SubstrateBoardEvaluator(
        IReadOnlyDictionary<Hash128, (double EffMu, double Rd, double Witnesses)> values,
        double cpPerPoint = 8d, int capCp = 200)
    {
        _epoch = static () => 0;
        _cpPerPoint = cpPerPoint;
        _capCp = Math.Max(0, capCp);
        _snapshot = SnapshotFrom(values);
        _snapshot.Generation = 1;
    }

    internal SubstrateBoardEvaluator(
        Func<IReadOnlyDictionary<Hash128, (double EffMu, double Rd, double Witnesses)>> loadValues,
        Func<long> epoch, double cpPerPoint = 8d, int capCp = 200)
    {
        _loadValues = loadValues;
        _epoch = epoch;
        _cpPerPoint = cpPerPoint;
        _capCp = Math.Max(0, capCp);
        _snapshot = SnapshotFrom(loadValues());
        _snapshot.Generation = 1;
        _observedEpoch = _epoch();
    }

    public long Version => Volatile.Read(ref _snapshot).Generation;

    public ISearchPositionEvaluator PrepareSearch()
    {
        long epoch = _epoch();
        if ((_ds is not null || _loadValues is not null)
            && epoch != Volatile.Read(ref _observedEpoch))
        {
            lock (_refreshGate)
            {
                epoch = _epoch();
                if (epoch != _observedEpoch)
                {
                    var next = _ds is not null
                        ? ReadSnapshot(_ds)
                        : SnapshotFrom(_loadValues!());
                    next.Generation = Volatile.Read(ref _snapshot).Generation + 1;
                    Volatile.Write(ref _snapshot, next);
                    Volatile.Write(ref _observedEpoch, epoch);
                }
            }
        }
        var snapshot = Volatile.Read(ref _snapshot);
        return new SearchSnapshot(this, snapshot, snapshot.Generation);
    }

    public int Evaluate(Board board) => Evaluate(board, Volatile.Read(ref _snapshot));

    private int Evaluate(Board board, Snapshot snapshot)
    {
        Interlocked.Increment(ref _positionReads);
        double sum = 0d, weightSum = 0d;

        Add(snapshot.Side[board.WhiteToMove ? 1 : 0], ref sum, ref weightSum);
        Add(snapshot.Castling[ChessPositionIdentity.CastlingDestinationMask(board)], ref sum, ref weightSum);
        int ep = ChessModality.CapturableEpSquare(board);
        Add(snapshot.EnPassant[ep < 0 ? 64 : (Board.RankOf(ep) << 3) | Board.FileOf(ep)], ref sum, ref weightSum);

        for (int square = 0; square < 128; square++)
        {
            if ((square & 0x88) != 0) { square += 7; continue; }
            Piece piece = board.Squares[square];
            if (piece == Piece.Empty) continue;
            int bit = (Board.RankOf(square) << 3) | Board.FileOf(square);
            Add(snapshot.PieceSquare[ChessPositionIdentity.PieceOrdinal(piece) * 64 + bit],
                ref sum, ref weightSum);
        }
        if (weightSum == 0d) return 0;
        Interlocked.Increment(ref _positionsWithEvidence);

        // Stored constituent outcomes use White's fixed POV. Negamax needs side-to-move POV.
        double whitePoints = sum / weightSum / 1e9;
        double stmPoints = board.WhiteToMove ? whitePoints : -whitePoints;
        return Math.Clamp((int)Math.Round(stmPoints * _cpPerPoint), -_capCp, _capCp);
    }

    public int LoadedAtoms => Volatile.Read(ref _snapshot).LoadedAtoms;
    public long EvidenceGeneration => Version;
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

    private static Snapshot ReadSnapshot(NpgsqlDataSource ds)
    {
        var slots = AtomUniverse();
        var edgeIds = slots.Select(static slot => ConsensusKeys.EdgeId(
            slot.Id, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject)).ToArray();
        var rows = NpgsqlConsensusByIds.Read(ds, edgeIds, ChessVocabulary.OutcomeType);
        var snapshot = new Snapshot();
        for (int i = 0; i < slots.Count; i++)
        {
            if (!rows.TryGetValue(edgeIds[i], out var row)) continue;
            Set(snapshot, slots[i], new AtomValue(row.EffMu, row.Rd, row.Witnesses, true));
            snapshot.LoadedAtoms++;
        }
        return snapshot;
    }

    private static Snapshot SnapshotFrom(
        IReadOnlyDictionary<Hash128, (double EffMu, double Rd, double Witnesses)> values)
    {
        var snapshot = new Snapshot();
        foreach (var slot in AtomUniverse())
        {
            if (!values.TryGetValue(slot.Id, out var value)) continue;
            Set(snapshot, slot, new AtomValue(value.EffMu, value.Rd, value.Witnesses, true));
            snapshot.LoadedAtoms++;
        }
        return snapshot;
    }

    private static void Set(Snapshot snapshot, AtomSlot slot, AtomValue value)
    {
        switch (slot.Kind)
        {
            case AtomKind.Side: snapshot.Side[slot.Index] = value; break;
            case AtomKind.Castling: snapshot.Castling[slot.Index] = value; break;
            case AtomKind.EnPassant: snapshot.EnPassant[slot.Index] = value; break;
            case AtomKind.PieceSquare: snapshot.PieceSquare[slot.Index] = value; break;
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
