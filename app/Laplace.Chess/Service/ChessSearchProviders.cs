using global::Npgsql;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// What one completed chess search actually consumed. Availability is deliberately not a
/// substitute for participation: every counter below is incremented on the move-selection hot
/// path, while learned-PST coverage records whether the loaded residual can change evaluation.
/// </summary>
public sealed record ChessSearchProviderReceipt(
    bool SubstrateSelected,
    bool LearnedPstSelected,
    bool LearnedPstContributes,
    int LearnedPstNonZeroCells,
    long RootSteerReads,
    long RootMovesInfluenced,
    long PositionEvidenceReads,
    long PositionEvidenceContributions,
    int SyzygyLargestMen,
    long SyzygyProbes,
    long SyzygyHits)
{
    public static ChessSearchProviderReceipt Classical { get; } = new(
        false, false, false, 0, 0, 0, 0, 0, 0, 0, 0);

    public string Summary => SubstrateSelected
        ? $"root={RootSteerReads}/{RootMovesInfluenced} " +
          $"position={PositionEvidenceReads}/{PositionEvidenceContributions} " +
          $"learned-pst={(LearnedPstContributes ? $"active:{LearnedPstNonZeroCells}" : "selected:no-delta")} " +
          $"syzygy={SyzygyHits}/{SyzygyProbes}({SyzygyLargestMen}-men)"
        : "classical-only";
}

/// <summary>
/// One immutable provider selection for one Search configuration. The database-backed readers
/// are shared by <see cref="ChessSearchProviders"/>, but the counters are private to this
/// configuration, so concurrent searches cannot contaminate each other's receipts.
/// </summary>
public sealed class ChessSearchConfiguration
{
    private sealed class Counters
    {
        public long RootReads;
        public long RootMoves;
        public long PositionReads;
        public long PositionContributions;
        public long TablebaseProbes;
        public long TablebaseHits;
    }

    private sealed class CountingRootBias(IRootBias inner, Counters counters) : IRootBias
    {
        public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
        {
            Interlocked.Increment(ref counters.RootReads);
            var bonus = inner.Bonus(root, moves);
            long influenced = 0;
            for (int i = 0; i < bonus.Length; i++)
                if (bonus[i] != 0) influenced++;
            if (influenced != 0)
                Interlocked.Add(ref counters.RootMoves, influenced);
            return bonus;
        }
    }

    private sealed class CountingPositionEvaluator(
        ISearchPositionEvaluator inner, Counters counters) : ISearchPositionEvaluator
    {
        public long Version => inner.Version;

        public ISearchPositionEvaluator PrepareSearch()
            => new CountingPositionEvaluator(inner.PrepareSearch(), counters);

        public int Evaluate(Board board)
        {
            Interlocked.Increment(ref counters.PositionReads);
            int cp = inner.Evaluate(board);
            if (cp != 0)
                Interlocked.Increment(ref counters.PositionContributions);
            return cp;
        }
    }

    private readonly Counters _counters = new();
    private readonly bool _substrate;
    private readonly bool _learnedSelected;
    private readonly bool _learnedContributes;
    private readonly int _learnedNonZeroCells;
    private readonly int _syzygyLargest;

    internal ChessSearchConfiguration(
        bool substrate,
        IRootBias? rootBias,
        ISearchPositionEvaluator? positionEvaluator,
        int[][]? mgPst,
        int[][]? egPst,
        bool learnedSelected,
        bool learnedContributes,
        int learnedNonZeroCells)
    {
        _substrate = substrate;
        _learnedSelected = learnedSelected;
        _learnedContributes = learnedContributes;
        _learnedNonZeroCells = learnedNonZeroCells;
        _syzygyLargest = substrate ? ChessTablebaseRuntime.Largest : 0;

        RootBias = rootBias is null ? null : new CountingRootBias(rootBias, _counters);
        PositionEvaluator = positionEvaluator is null
            ? null
            : new CountingPositionEvaluator(positionEvaluator, _counters);
        MgPst = mgPst;
        EgPst = egPst;
        Tablebase = substrate ? ProbeTablebase : null;
    }

    public IRootBias? RootBias { get; }
    public ISearchPositionEvaluator? PositionEvaluator { get; }
    public int[][]? MgPst { get; }
    public int[][]? EgPst { get; }
    public Func<Board, SearchTablebaseVerdict?>? Tablebase { get; }

    public Search BuildSearch(int ttBits = 20)
        => new(
            EvalTerm.All, RootBias, ttBits,
            mgPst: MgPst, egPst: EgPst,
            positionEvaluator: PositionEvaluator,
            tablebase: Tablebase);

    public void ApplyTo(Search search)
        => search.Reconfigure(RootBias, MgPst, EgPst, PositionEvaluator, Tablebase);

    public ChessSearchProviderReceipt Receipt()
        => !_substrate
            ? ChessSearchProviderReceipt.Classical
            : new ChessSearchProviderReceipt(
                true,
                _learnedSelected,
                _learnedContributes,
                _learnedNonZeroCells,
                Volatile.Read(ref _counters.RootReads),
                Volatile.Read(ref _counters.RootMoves),
                Volatile.Read(ref _counters.PositionReads),
                Volatile.Read(ref _counters.PositionContributions),
                _syzygyLargest,
                Volatile.Read(ref _counters.TablebaseProbes),
                Volatile.Read(ref _counters.TablebaseHits));

    private SearchTablebaseVerdict? ProbeTablebase(Board board)
    {
        Interlocked.Increment(ref _counters.TablebaseProbes);
        var result = ChessTablebaseRuntime.ProbeSearch(board);
        if (result is not null)
            Interlocked.Increment(ref _counters.TablebaseHits);
        return result;
    }
}

/// <summary>
/// Shared, pre-move-clock provider state for API Play, Lichess and UCI. Loading learned PST here
/// is intentional: provider preparation happens during engine/session initialization, never from
/// inside an alpha-beta node or after a move clock has started.
/// </summary>
public sealed class ChessSearchProviders
{
    private sealed record LearnedSnapshot(
        int[][] Mg,
        int[][] Eg,
        bool Contributes,
        int NonZeroCells);

    private readonly NpgsqlDataSource _ds;
    private readonly SubstrateRootBias _rootBias;
    private readonly SubstrateBoardEvaluator _positionEvaluator;
    private readonly object _learnedGate = new();
    private LearnedSnapshot _learned;

    public ChessSearchProviders(NpgsqlDataSource ds)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _rootBias = new SubstrateRootBias(ds);
        _positionEvaluator = new SubstrateBoardEvaluator(ds);
        _learned = LoadLearned(ds);
    }

    public ChessSearchConfiguration Configure(bool substrate)
    {
        if (!substrate)
            return new ChessSearchConfiguration(
                false, null, null, null, null,
                learnedSelected: false, learnedContributes: false, learnedNonZeroCells: 0);

        LearnedSnapshot learned;
        lock (_learnedGate) learned = _learned;
        return new ChessSearchConfiguration(
            true,
            _rootBias,
            _positionEvaluator,
            learned.Mg,
            learned.Eg,
            learnedSelected: true,
            learned.Contributes,
            learned.NonZeroCells);
    }

    /// <summary>
    /// A completed recorded game can change move OUTCOME cells. Refresh outside the next search
    /// so the following move/game consumes the newly folded learned residual instead of merely
    /// displaying it in the PST panel.
    /// </summary>
    public void RefreshLearnedPst()
    {
        var next = LoadLearned(_ds);
        lock (_learnedGate) _learned = next;
    }

    private static LearnedSnapshot LoadLearned(NpgsqlDataSource ds)
    {
        var (deltaMg, deltaEg) = LearnedPst.BuildTables(ds);
        int nonZero = 0;
        for (int p = 0; p < deltaMg.Length; p++)
        {
            for (int sq = 0; sq < deltaMg[p].Length; sq++)
                if (deltaMg[p][sq] != 0 || deltaEg[p][sq] != 0) nonZero++;
        }
        var (mg, eg) = Evaluation.BlendPeStoWith(deltaMg, deltaEg);
        return new LearnedSnapshot(mg, eg, nonZero != 0, nonZero);
    }
}
