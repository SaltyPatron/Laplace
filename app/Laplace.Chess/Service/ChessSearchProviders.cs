using global::Npgsql;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// What one completed chess search actually consumed. Availability is deliberately not a
/// substitute for participation: dynamic counters are incremented on the move-selection hot
/// path, while coverage describes prepared provider state before the clock starts.
/// </summary>
public sealed record ChessSearchProviderReceipt(
    bool SubstrateSelected,
    bool LearnedPstSelected,
    bool LearnedPstContributes,
    int LearnedPstNonZeroCells,
    long LearnedPstReads,
    long LearnedPstContributions,
    long RootSteerReads,
    long RootMovesInfluenced,
    long PositionEvidenceReads,
    long PositionEvidenceContributions,
    int SyzygyLargestMen,
    long SyzygyProbes,
    long SyzygyHits)
{
    public static ChessSearchProviderReceipt Classical { get; } = new(
        false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public string Summary => SubstrateSelected
        ? $"root={RootSteerReads}/{RootMovesInfluenced} " +
          $"position={PositionEvidenceReads}/{PositionEvidenceContributions} " +
          $"learned-pst={LearnedPstReads}/{LearnedPstContributions}" +
          $"(cells:{LearnedPstNonZeroCells}) " +
          $"syzygy={SyzygyHits}/{SyzygyProbes}({SyzygyLargestMen}-men)"
        : "classical-only";
}

/// <summary>
/// One immutable provider selection for one Search configuration. Database-backed readers are
/// shared by <see cref="ChessSearchProviders"/>, but the counters are private to this
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
        public long LearnedReads;
        public long LearnedContributions;
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
            if (inner is IChessSearchPositionPlanes planes)
            {
                var score = planes.EvaluatePlanes(board);
                if (score.AtomOutcomeCp != 0)
                    Interlocked.Increment(ref counters.PositionContributions);
                if (score.LearnedPstCp != 0)
                    Interlocked.Increment(ref counters.LearnedContributions);
                // A non-zero learned table is selected at every static leaf even when this
                // particular board's centred residual happens to cancel to zero.
                if (score.LearnedPstCp != 0 || inner is not null)
                    Interlocked.Increment(ref counters.LearnedReads);
                return score.TotalCp;
            }

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
        Tablebase = substrate ? ProbeTablebase : null;
    }

    public IRootBias? RootBias { get; }
    public ISearchPositionEvaluator? PositionEvaluator { get; }
    public Func<Board, SearchTablebaseVerdict?>? Tablebase { get; }

    public Search BuildSearch(int ttBits = 20)
        => new(
            EvalTerm.All, RootBias, ttBits,
            positionEvaluator: PositionEvaluator,
            tablebase: Tablebase);

    public void ApplyTo(Search search)
        => search.Reconfigure(RootBias, null, null, PositionEvaluator, Tablebase);

    public ChessSearchProviderReceipt Receipt()
        => !_substrate
            ? ChessSearchProviderReceipt.Classical
            : new ChessSearchProviderReceipt(
                true,
                _learnedSelected,
                _learnedContributes,
                _learnedNonZeroCells,
                Volatile.Read(ref _counters.LearnedReads),
                Volatile.Read(ref _counters.LearnedContributions),
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
/// Shared, pre-move-clock provider state for API Play, Lichess and UCI. The bounded learned PST
/// is part of <see cref="SubstrateBoardEvaluator"/>'s immutable leaf snapshot, so every caller
/// that already selected substrate search now consumes it without a route-private side channel.
/// </summary>
public sealed class ChessSearchProviders
{
    private readonly SubstrateRootBias _rootBias;
    private readonly SubstrateBoardEvaluator _positionEvaluator;

    public ChessSearchProviders(NpgsqlDataSource ds)
    {
        ArgumentNullException.ThrowIfNull(ds);
        _rootBias = new SubstrateRootBias(ds);
        _positionEvaluator = new SubstrateBoardEvaluator(ds);
    }

    public ChessSearchConfiguration Configure(bool substrate)
    {
        if (!substrate)
            return new ChessSearchConfiguration(
                false, null, null,
                learnedSelected: false, learnedContributes: false, learnedNonZeroCells: 0);

        int learnedCells = _positionEvaluator.LearnedPstNonZeroCells;
        return new ChessSearchConfiguration(
            true,
            _rootBias,
            _positionEvaluator,
            learnedSelected: true,
            learnedContributes: learnedCells != 0,
            learnedNonZeroCells: learnedCells);
    }

    /// <summary>
    /// Force a fresh immutable leaf snapshot outside the next move clock. This is used by UCI
    /// ucinewgame for updates that may have arrived from another process; in-process live games
    /// also advance the normal evidence epoch and refresh automatically on PrepareSearch.
    /// </summary>
    public void RefreshLearnedPst() => _positionEvaluator.Refresh();
}
