using global::Npgsql;
using System.Diagnostics;
using System.Globalization;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// What one completed chess search actually consumed. Availability is deliberately not a
/// substitute for participation: a provider can be selected/prepared yet still report zero
/// reads or zero score-changing contributions for a particular search.
/// </summary>
public sealed record ChessSearchProviderReceipt(
    bool SubstrateSelected,
    int PositionAtomsLoaded,
    long PositionEvidenceReads,
    long PositionEvidenceContributions,
    bool LearnedPstSelected,
    bool LearnedPstContributes,
    int LearnedPstNonZeroCells,
    long LearnedPstReads,
    long LearnedPstContributions,
    bool TacticSelected,
    bool TacticContributes,
    int TacticPatternsLoaded,
    long TacticReads,
    long TacticContributions,
    long RootSteerReads,
    long RootMovesInfluenced,
    int SyzygyLargestMen,
    long SyzygyProbes,
    long SyzygyHits)
{
    // Root work is the shared provider's counter change during this configuration's calls.
    // Concurrent API callers can overlap these deltas; serial UCI searches do not.
    // Inclusive root time already contains frontier/read time: never sum them together.
    // Snapshot preparation is outside Search's iteration clock, inside UCI's final go time.
    public string RootWorkScope { get; init; } = "unavailable";
    public long RootBackendReads { get; init; }
    public long RootEvidenceCacheHits { get; init; }
    public long RootFrontierBuilds { get; init; }
    public long RootTransitionPerfcacheHits { get; init; }
    public long RootTransitionNovelHits { get; init; }
    public long RootTransitionCompositions { get; init; }
    public double RootInclusiveMilliseconds { get; init; }
    public double RootFrontierMilliseconds { get; init; }
    public double RootEvidenceReadMilliseconds { get; init; }
    public long SnapshotPreparations { get; init; }
    public double SnapshotPreparationMilliseconds { get; init; }

    private static string Ms(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    public static ChessSearchProviderReceipt Classical { get; } = new(
        false,
        0, 0, 0,
        false, false, 0, 0, 0,
        false, false, 0, 0, 0,
        0, 0,
        0, 0, 0);

    public string Summary => SubstrateSelected
        ? $"root={RootSteerReads}/{RootMovesInfluenced} " +
          $"position={PositionEvidenceReads}/{PositionEvidenceContributions}" +
          $"(atoms:{PositionAtomsLoaded}) " +
          $"learned-pst={LearnedPstReads}/{LearnedPstContributions}" +
          $"(cells:{LearnedPstNonZeroCells}) " +
          $"tactics={TacticReads}/{TacticContributions}" +
          $"(patterns:{TacticPatternsLoaded}) " +
          $"syzygy={SyzygyHits}/{SyzygyProbes}({SyzygyLargestMen}-men) " +
          $"root-work-scope={RootWorkScope} " +
          $"root-backend={RootBackendReads} root-cache={RootEvidenceCacheHits} " +
          $"root-frontiers={RootFrontierBuilds} " +
          $"root-transitions={RootTransitionPerfcacheHits}/{RootTransitionNovelHits}/{RootTransitionCompositions} " +
          $"root-ms-inclusive={Ms(RootInclusiveMilliseconds)} " +
          $"frontier-ms={Ms(RootFrontierMilliseconds)} evidence-read-ms={Ms(RootEvidenceReadMilliseconds)} " +
          $"snapshot-prepare={SnapshotPreparations}/{Ms(SnapshotPreparationMilliseconds)}ms"
        : "classical-only";
}

/// <summary>
/// One immutable provider selection for one Search configuration. Database-backed readers are
/// shared by <see cref="ChessSearchProviders"/>. Usage counters and enclosing timers belong
/// to this configuration. Root work deltas observe the shared provider instance and can overlap
/// concurrent callers; the receipt explicitly labels that scope.
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
        public long TacticReads;
        public long TacticContributions;
        public long TablebaseProbes;
        public long TablebaseHits;
        public long RootBackendReads, RootEvidenceCacheHits, RootFrontierBuilds;
        public long RootTransitionPerfcacheHits, RootTransitionNovelHits, RootTransitionCompositions;
        public long RootTicks, RootFrontierTicks, RootEvidenceReadTicks;
        public long SnapshotPreparations, SnapshotPreparationTicks;
    }

    private sealed class CountingRootBias(IRootBias inner, Counters counters) : IRootBias
    {
        public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
        {
            Interlocked.Increment(ref counters.RootReads);
            var observed = inner as SubstrateRootBias;
            var before = observed?.ObserveWork();
            long started = Stopwatch.GetTimestamp();
            try
            {
                var bonus = inner.Bonus(root, moves);
                long influenced = 0;
                for (int i = 0; i < bonus.Length; i++)
                    if (bonus[i] != 0) influenced++;
                if (influenced != 0)
                    Interlocked.Add(ref counters.RootMoves, influenced);
                return bonus;
            }
            finally
            {
                Interlocked.Add(ref counters.RootTicks, Stopwatch.GetTimestamp() - started);
                if (before is { } prior && observed is not null)
                {
                    var after = observed.ObserveWork();
                    Interlocked.Add(ref counters.RootBackendReads, after.BackendReads - prior.BackendReads);
                    Interlocked.Add(ref counters.RootEvidenceCacheHits, after.EvidenceCacheHits - prior.EvidenceCacheHits);
                    Interlocked.Add(ref counters.RootFrontierBuilds, after.FrontierBuilds - prior.FrontierBuilds);
                    Interlocked.Add(ref counters.RootTransitionPerfcacheHits, after.TransitionPerfcacheHits - prior.TransitionPerfcacheHits);
                    Interlocked.Add(ref counters.RootTransitionNovelHits, after.TransitionNovelHits - prior.TransitionNovelHits);
                    Interlocked.Add(ref counters.RootTransitionCompositions, after.TransitionCompositions - prior.TransitionCompositions);
                    Interlocked.Add(ref counters.RootFrontierTicks, after.FrontierTicks - prior.FrontierTicks);
                    Interlocked.Add(ref counters.RootEvidenceReadTicks, after.EvidenceReadTicks - prior.EvidenceReadTicks);
                }
            }
        }
    }

    private sealed class CountingPositionEvaluator(
        ISearchPositionEvaluator inner,
        Counters counters,
        bool atomActive,
        bool learnedActive,
        bool tacticActive) : ISearchPositionEvaluator
    {
        public long Version => inner.Version;

        public ISearchPositionEvaluator PrepareSearch()
        {
            Interlocked.Increment(ref counters.SnapshotPreparations);
            long started = Stopwatch.GetTimestamp();
            try
            {
                return new CountingPositionEvaluator(
                    inner.PrepareSearch(), counters, atomActive, learnedActive, tacticActive);
            }
            finally
            {
                Interlocked.Add(ref counters.SnapshotPreparationTicks, Stopwatch.GetTimestamp() - started);
            }
        }

        public int Evaluate(Board board)
        {
            if (inner is IChessSearchPositionPlanes planes)
            {
                var score = planes.EvaluatePlanes(board);
                if (atomActive)
                {
                    Interlocked.Increment(ref counters.PositionReads);
                    if (score.AtomOutcomeCp != 0)
                        Interlocked.Increment(ref counters.PositionContributions);
                }
                if (learnedActive)
                {
                    Interlocked.Increment(ref counters.LearnedReads);
                    if (score.LearnedPstCp != 0)
                        Interlocked.Increment(ref counters.LearnedContributions);
                }
                if (tacticActive)
                {
                    Interlocked.Increment(ref counters.TacticReads);
                    if (score.TacticOutcomeCp != 0)
                        Interlocked.Increment(ref counters.TacticContributions);
                }
                return score.TotalCp;
            }

            // Non-plane evaluators still count as the generic position-evidence provider.
            if (atomActive) Interlocked.Increment(ref counters.PositionReads);
            int cp = inner.Evaluate(board);
            if (atomActive && cp != 0)
                Interlocked.Increment(ref counters.PositionContributions);
            return cp;
        }
    }

    private readonly Counters _counters = new();
    private readonly bool _substrate;
    private readonly int _positionAtomsLoaded;
    private readonly bool _learnedSelected;
    private readonly bool _learnedContributes;
    private readonly int _learnedNonZeroCells;
    private readonly bool _tacticSelected;
    private readonly bool _tacticContributes;
    private readonly int _tacticPatternsLoaded;
    private readonly int _syzygyLargest;
    private readonly bool _rootWorkAvailable;

    internal ChessSearchConfiguration(
        bool substrate,
        IRootBias? rootBias,
        ISearchPositionEvaluator? positionEvaluator,
        bool learnedSelected,
        bool learnedContributes,
        int learnedNonZeroCells,
        int positionAtomsLoaded = 1,
        bool tacticSelected = false,
        bool tacticContributes = false,
        int tacticPatternsLoaded = 0)
    {
        _substrate = substrate;
        _rootWorkAvailable = substrate && rootBias is SubstrateRootBias;
        _positionAtomsLoaded = substrate ? Math.Max(0, positionAtomsLoaded) : 0;
        _learnedSelected = substrate && learnedSelected;
        _learnedContributes = substrate && learnedContributes;
        _learnedNonZeroCells = substrate ? Math.Max(0, learnedNonZeroCells) : 0;
        _tacticSelected = substrate && tacticSelected;
        _tacticContributes = substrate && tacticContributes;
        _tacticPatternsLoaded = substrate ? Math.Max(0, tacticPatternsLoaded) : 0;
        _syzygyLargest = substrate ? ChessTablebaseRuntime.Largest : 0;

        RootBias = rootBias is null ? null : new CountingRootBias(rootBias, _counters);
        PositionEvaluator = positionEvaluator is null
            ? null
            : new CountingPositionEvaluator(
                positionEvaluator,
                _counters,
                atomActive: _positionAtomsLoaded > 0,
                learnedActive: _learnedContributes,
                tacticActive: _tacticContributes);
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
                _positionAtomsLoaded,
                Volatile.Read(ref _counters.PositionReads),
                Volatile.Read(ref _counters.PositionContributions),
                _learnedSelected,
                _learnedContributes,
                _learnedNonZeroCells,
                Volatile.Read(ref _counters.LearnedReads),
                Volatile.Read(ref _counters.LearnedContributions),
                _tacticSelected,
                _tacticContributes,
                _tacticPatternsLoaded,
                Volatile.Read(ref _counters.TacticReads),
                Volatile.Read(ref _counters.TacticContributions),
                Volatile.Read(ref _counters.RootReads),
                Volatile.Read(ref _counters.RootMoves),
                _syzygyLargest,
                Volatile.Read(ref _counters.TablebaseProbes),
                Volatile.Read(ref _counters.TablebaseHits))
            {
                RootWorkScope = _rootWorkAvailable ? "provider-instance-deltas" : "unavailable",
                RootBackendReads = Volatile.Read(ref _counters.RootBackendReads),
                RootEvidenceCacheHits = Volatile.Read(ref _counters.RootEvidenceCacheHits),
                RootFrontierBuilds = Volatile.Read(ref _counters.RootFrontierBuilds),
                RootTransitionPerfcacheHits = Volatile.Read(ref _counters.RootTransitionPerfcacheHits),
                RootTransitionNovelHits = Volatile.Read(ref _counters.RootTransitionNovelHits),
                RootTransitionCompositions = Volatile.Read(ref _counters.RootTransitionCompositions),
                RootInclusiveMilliseconds = Milliseconds(ref _counters.RootTicks),
                RootFrontierMilliseconds = Milliseconds(ref _counters.RootFrontierTicks),
                RootEvidenceReadMilliseconds = Milliseconds(ref _counters.RootEvidenceReadTicks),
                SnapshotPreparations = Volatile.Read(ref _counters.SnapshotPreparations),
                SnapshotPreparationMilliseconds = Milliseconds(ref _counters.SnapshotPreparationTicks),
            };

    private static double Milliseconds(ref long ticks)
        => Volatile.Read(ref ticks) * (1000d / Stopwatch.Frequency);

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
/// Shared, pre-move-clock provider state for API Play, Lichess and UCI. Bounded atom, learned-PST
/// and tactical-pattern state is prepared in <see cref="SubstrateBoardEvaluator"/> so every
/// substrate-enabled Search consumes the same provider set instead of route-private approximations.
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
                learnedSelected: false,
                learnedContributes: false,
                learnedNonZeroCells: 0,
                positionAtomsLoaded: 0);

        int learnedCells = _positionEvaluator.LearnedPstNonZeroCells;
        int tacticPatterns = _positionEvaluator.LoadedTacticPatterns;
        return new ChessSearchConfiguration(
            true,
            _rootBias,
            _positionEvaluator,
            learnedSelected: true,
            learnedContributes: learnedCells != 0,
            learnedNonZeroCells: learnedCells,
            positionAtomsLoaded: _positionEvaluator.LoadedAtoms,
            tacticSelected: true,
            tacticContributes: tacticPatterns != 0,
            tacticPatternsLoaded: tacticPatterns);
    }

    /// <summary>
    /// Force a fresh immutable leaf snapshot outside the next move clock. This is used by UCI
    /// ucinewgame for updates that may have arrived from another process; in-process live games
    /// also advance the normal evidence epoch and refresh automatically on PrepareSearch.
    /// </summary>
    public void RefreshLearnedPst() => _positionEvaluator.Refresh();
}
