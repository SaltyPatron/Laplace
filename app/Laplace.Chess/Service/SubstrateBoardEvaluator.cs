using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>Typed leaf contributions kept distinct until the active chess search combines them.</summary>
public readonly record struct ChessPositionPlaneScore(int TotalCp, int AtomOutcomeCp, int LearnedPstCp);

/// <summary>
/// Search-position evaluator that can expose the typed contribution split used to produce its
/// scalar search value. Search itself still consumes <see cref="ISearchPositionEvaluator"/>;
/// receipt wrappers can retain the plane boundary instead of pretending one opaque cp number is
/// proof that every advertised provider participated.
/// </summary>
public interface IChessSearchPositionPlanes : ISearchPositionEvaluator
{
    ChessPositionPlaneScore EvaluatePlanes(Board board);
}

/// <summary>
/// Immutable, bounded substrate snapshot used at every search leaf without database queries.
/// Two independently inspectable planes participate here:
/// (1) OUTCOME consensus on reusable board atoms; and
/// (2) the learned PST residual projected from move OUTCOME consensus.
/// The classical material/PeSTO/tactical proposal remains in <see cref="Evaluation"/>; these are
/// learned substrate additions to that deterministic floor, not replacements for it.
/// </summary>
public sealed class SubstrateBoardEvaluator : IChessSearchPositionPlanes
{
    private sealed class Snapshot
    {
        public readonly AtomValue[] Side = new AtomValue[2];
        public readonly AtomValue[] Castling = new AtomValue[16];
        public readonly AtomValue[] EnPassant = new AtomValue[65];
        public readonly AtomValue[] PieceSquare = new AtomValue[12 * 64];
        public int[][]? LearnedMg;
        public int[][]? LearnedEg;
        public int LearnedNonZeroCells;
        public int LoadedAtoms;
        public long Generation;
    }

    private sealed class SearchSnapshot(
        SubstrateBoardEvaluator owner, Snapshot snapshot, long version) : IChessSearchPositionPlanes
    {
        public long Version => version;
        public int Evaluate(Board board) => EvaluatePlanes(board).TotalCp;
        public ChessPositionPlaneScore EvaluatePlanes(Board board) => owner.EvaluatePlanes(board, snapshot);
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
    private long _learnedPstReads;
    private long _learnedPstContributions;

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
                    // The same completed game that advances transition observations can also
                    // advance move-OUTCOME cells. Refresh both atom consensus and learned PST as
                    // one immutable generation so a search never mixes old/new provider state.
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

    public int Evaluate(Board board) => EvaluatePlanes(board).TotalCp;

    public ChessPositionPlaneScore EvaluatePlanes(Board board)
        => EvaluatePlanes(board, Volatile.Read(ref _snapshot));

    private ChessPositionPlaneScore EvaluatePlanes(Board board, Snapshot snapshot)
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

        int atomCp = 0;
        if (weightSum != 0d)
        {
            Interlocked.Increment(ref _positionsWithEvidence);
            // Stored constituent outcomes use White's fixed POV. Negamax needs side-to-move POV.
            double whitePoints = sum / weightSum / 1e9;
            double stmPoints = board.WhiteToMove ? whitePoints : -whitePoints;
            atomCp = Math.Clamp((int)Math.Round(stmPoints * _cpPerPoint), -_capCp, _capCp);
        }

        int learnedCp = 0;
        if (snapshot.LearnedMg is not null && snapshot.LearnedEg is not null
            && snapshot.LearnedNonZeroCells != 0)
        {
            Interlocked.Increment(ref _learnedPstReads);
            // BuildTables returns the learned RESIDUAL, not PeSTO itself. Evaluating only the PST
            // term over these delta tables gives exactly the corpus contribution to add beside
            // the ordinary deterministic Evaluation result already calculated by Search.
            learnedCp = Evaluation.Evaluate(
                board, EvalTerm.Pst, snapshot.LearnedMg, snapshot.LearnedEg);
            if (learnedCp != 0)
                Interlocked.Increment(ref _learnedPstContributions);
        }

        return new ChessPositionPlaneScore(atomCp + learnedCp, atomCp, learnedCp);
    }

    public int LoadedAtoms => Volatile.Read(ref _snapshot).LoadedAtoms;
    public int LearnedPstNonZeroCells => Volatile.Read(ref _snapshot).LearnedNonZeroCells;
    public long EvidenceGeneration => Version;
    public long PositionReads => Volatile.Read(ref _positionReads);
    public long PositionsWithEvidence => Volatile.Read(ref _positionsWithEvidence);
    public long LearnedPstReads => Volatile.Read(ref _learnedPstReads);
    public long LearnedPstContributions => Volatile.Read(ref _learnedPstContributions);

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

        // Learned PST is also bounded state: 384 residual cells derived from the fixed move
        // vocabulary. Prepare it outside the search clock and carry the arrays in the same
        // immutable generation as the atom census.
        var (learnedMg, learnedEg) = LearnedPst.BuildTables(ds);
        snapshot.LearnedMg = learnedMg;
        snapshot.LearnedEg = learnedEg;
        int nonZero = 0;
        for (int piece = 0; piece < learnedMg.Length; piece++)
            for (int square = 0; square < learnedMg[piece].Length; square++)
                if (learnedMg[piece][square] != 0 || learnedEg[piece][square] != 0)
                    nonZero++;
        snapshot.LearnedNonZeroCells = nonZero;
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
