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
    long RootSteerReads,
    long RootMovesInfluenced,
    int SyzygyLargestMen,
    long SyzygyProbes,
    long SyzygyHits)
{
    // Root work is the shared provider's counter change during this configuration's calls.
    // Concurrent API callers can overlap these deltas; serial UCI searches do not.
    // Inclusive root time already contains frontier/read time: never sum them together.
    public string RootWorkScope { get; init; } = "unavailable";
    public long RootBackendReads { get; init; }
    public long RootEvidenceCacheHits { get; init; }
    public long RootFrontierBuilds { get; init; }
    public double RootInclusiveMilliseconds { get; init; }
    public double RootFrontierMilliseconds { get; init; }
    public double RootEvidenceReadMilliseconds { get; init; }

    private static string Ms(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    public static ChessSearchProviderReceipt Classical { get; } = new(false, 0, 0, 0, 0, 0);

    public string Summary => SubstrateSelected
        ? $"root={RootSteerReads}/{RootMovesInfluenced} " +
          $"syzygy={SyzygyHits}/{SyzygyProbes}({SyzygyLargestMen}-men) " +
          $"root-work-scope={RootWorkScope} " +
          $"root-backend={RootBackendReads} root-cache={RootEvidenceCacheHits} " +
          $"root-frontiers={RootFrontierBuilds} " +
          $"root-ms-inclusive={Ms(RootInclusiveMilliseconds)} " +
          $"frontier-ms={Ms(RootFrontierMilliseconds)} evidence-read-ms={Ms(RootEvidenceReadMilliseconds)}"
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
        public long TablebaseProbes;
        public long TablebaseHits;
        public long RootBackendReads, RootEvidenceCacheHits, RootFrontierBuilds;
        public long RootTicks, RootFrontierTicks, RootEvidenceReadTicks;
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
                    Interlocked.Add(ref counters.RootFrontierTicks, after.FrontierTicks - prior.FrontierTicks);
                    Interlocked.Add(ref counters.RootEvidenceReadTicks, after.EvidenceReadTicks - prior.EvidenceReadTicks);
                }
            }
        }
    }

    private readonly Counters _counters = new();
    private readonly bool _substrate;
    private readonly int _syzygyLargest;
    private readonly bool _rootWorkAvailable;

    internal ChessSearchConfiguration(bool substrate, IRootBias? rootBias)
    {
        _substrate = substrate;
        _rootWorkAvailable = substrate && rootBias is SubstrateRootBias;
        _syzygyLargest = substrate ? ChessTablebaseRuntime.Largest : 0;
        RootBias = rootBias is null ? null : new CountingRootBias(rootBias, _counters);
        Tablebase = substrate ? ProbeTablebase : null;
    }

    public IRootBias? RootBias { get; }
    public Func<Board, SearchTablebaseVerdict?>? Tablebase { get; }

    /// <summary>Seeded before Think. Replacing it does not clear the transposition table unless its evaluations change.</summary>
    public SearchContinuationTable? Continuations { get; set; }

    public Search BuildSearch(int ttBits = 20)
        => new(
            EvalTerm.All, RootBias, ttBits,
            tablebase: Tablebase,
            continuations: Continuations);

    public void ApplyTo(Search search)
        => search.Reconfigure(RootBias, null, null, null, Tablebase, Continuations);

    public ChessSearchProviderReceipt Receipt()
        => !_substrate
            ? ChessSearchProviderReceipt.Classical
            : new ChessSearchProviderReceipt(
                true,
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
                RootInclusiveMilliseconds = Milliseconds(ref _counters.RootTicks),
                RootFrontierMilliseconds = Milliseconds(ref _counters.RootFrontierTicks),
                RootEvidenceReadMilliseconds = Milliseconds(ref _counters.RootEvidenceReadTicks),
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
/// Provider state prepared before the move clock and shared by API Play, Lichess and UCI: the Native
/// standings of the moves recorded games played (<see cref="SubstrateRootBias"/>). No leaf evaluator reads
/// the substrate.
/// </summary>
public sealed class ChessSearchProviders
{
    private readonly SubstrateRootBias _rootBias;

    public ChessSearchProviders(NpgsqlDataSource ds)
    {
        ArgumentNullException.ThrowIfNull(ds);
        _rootBias = new SubstrateRootBias(ds);
    }

    public ChessSearchConfiguration Configure(bool substrate)
        => substrate ? new ChessSearchConfiguration(true, _rootBias) : new ChessSearchConfiguration(false, null);
}
