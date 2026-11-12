using global::Npgsql;
using System.Collections.Concurrent;
using System.Diagnostics;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Root-move evidence from the Native substrate: each legal move's [position, move, position] claim
/// (<see cref="ChessEngineIdentity.TransitionId"/>, the claim a recorded game attests, scored for the side
/// that made the move) and its standing (<c>public.laplace_standings</c>). A move no recorded game played
/// has no standing and no bonus. Rules the Engine's identity does not cover (drops, check counts, another
/// objective) have no recorded claims and read nothing.
/// </summary>
public sealed class SubstrateRootBias : IRootBias
{
    // Equality retains the complete ordered legal frontier, including flags/promotion.
    private sealed class FrontierKey(Hash128 root, IReadOnlyList<ChessMove> moves) : IEquatable<FrontierKey>
    {
        public readonly Hash128 Root = root;
        public readonly ChessMove[] Moves = moves.ToArray();
        public bool Equals(FrontierKey? other) => other is not null && Root == other.Root
            && Moves.AsSpan().SequenceEqual(other.Moves);
        public override bool Equals(object? other) => other is FrontierKey key && Equals(key);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Root);
            foreach (var move in Moves) hash.Add(move);
            return hash.ToHashCode();
        }
    }
    private sealed record CacheEntry(long ExpiresAt, Hash128[] Claims, Lazy<IReadOnlyDictionary<Hash128, NpgsqlChessTransitionStandings.Row>> Standings);

    private readonly double _cpPerPoint;
    private readonly int _capCp;
    private readonly double? _shrinkK0;
    private readonly Func<IReadOnlyCollection<Hash128>, IReadOnlyDictionary<Hash128, NpgsqlChessTransitionStandings.Row>> _read;
    private readonly ConcurrentDictionary<FrontierKey, CacheEntry> _cache = new();
    private readonly ConcurrentQueue<FrontierKey> _cacheOrder = new();
    private const int CacheCapacity = 16_384;
    private const long CacheLifetimeMilliseconds = 2_000;
    private readonly Func<long> _clock;
    private long _frontierBuilds;
    private long _evidenceCacheHits;
    private long _rootReads;
    private long _backendReads;
    private long _rootsWithExactEvidence;
    private long _exactTransitionSignals;
    private long _frontierTicks;
    private long _evidenceReadTicks;

    internal readonly record struct WorkSnapshot(
        long BackendReads, long EvidenceCacheHits, long FrontierBuilds, long FrontierTicks, long EvidenceReadTicks);

    // Provider-instance counters. Deltas are exclusive only when callers do not overlap,
    // as in one UCI engine's serial searches.
    internal WorkSnapshot ObserveWork() => new(
        BackendReads, EvidenceCacheHits, FrontierBuilds,
        Interlocked.Read(ref _frontierTicks), Interlocked.Read(ref _evidenceReadTicks));

    public long FrontierBuilds => Volatile.Read(ref _frontierBuilds);
    public long EvidenceCacheHits => Volatile.Read(ref _evidenceCacheHits);
    internal int CachedFrontiers => _cache.Count;
    internal int EvictionQueueCount => _cacheOrder.Count;
    public long RootReads => Volatile.Read(ref _rootReads);
    public long BackendReads => Volatile.Read(ref _backendReads);
    public long RootsWithExactEvidence => Volatile.Read(ref _rootsWithExactEvidence);
    public long ExactTransitionSignals => Volatile.Read(ref _exactTransitionSignals);

    public SubstrateRootBias(NpgsqlDataSource ds, double cpPerPoint = 8.0, int capCp = 150, double? shrinkK0 = null)
        : this(claims => NpgsqlChessTransitionStandings.Read(ds ?? throw new ArgumentNullException(nameof(ds)), claims),
            cpPerPoint, capCp, shrinkK0)
    {
        ArgumentNullException.ThrowIfNull(ds);
    }

    internal SubstrateRootBias(
        Func<IReadOnlyCollection<Hash128>, IReadOnlyDictionary<Hash128, NpgsqlChessTransitionStandings.Row>> read,
        double cpPerPoint = 8.0, int capCp = 150, double? shrinkK0 = null, Func<long>? clock = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _cpPerPoint = cpPerPoint;
        _capCp = capCp;
        _shrinkK0 = shrinkK0;
        _clock = clock ?? (static () => Environment.TickCount64);
    }

    public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
    {
        var bonus = new int[moves.Count];
        if (moves.Count == 0 || !ChessEngineIdentity.TryPositionId(root, out var rootId)) return bonus;
        Interlocked.Increment(ref _rootReads);

        var key = new FrontierKey(rootId, moves);
        CacheEntry entry;
        while (true)
        {
            _cache.TryGetValue(key, out var current);
            long now = _clock();
            if (current is not null && current.ExpiresAt > now)
            {
                entry = current;
                Interlocked.Increment(ref _evidenceCacheHits);
                break;
            }
            var claims = current?.Claims ?? Claims(root, rootId, key.Moves);
            var replacement = new CacheEntry(now + CacheLifetimeMilliseconds, claims,
                new Lazy<IReadOnlyDictionary<Hash128, NpgsqlChessTransitionStandings.Row>>(() =>
                {
                    Interlocked.Increment(ref _backendReads);
                    long started = Stopwatch.GetTimestamp();
                    try { return _read(claims); }
                    finally { Interlocked.Add(ref _evidenceReadTicks, Stopwatch.GetTimestamp() - started); }
                }, LazyThreadSafetyMode.ExecutionAndPublication));
            bool installed = current is null
                ? _cache.TryAdd(key, replacement)
                : _cache.TryUpdate(key, replacement, current);
            if (!installed) continue;
            entry = replacement;
            if (current is null) _cacheOrder.Enqueue(key);
            break;
        }
        TrimCache();
        IReadOnlyDictionary<Hash128, NpgsqlChessTransitionStandings.Row> standings;
        try { standings = entry.Standings.Value; }
        catch
        {
            // Remove only this failed observation; a newer one may already have replaced it.
            ((ICollection<KeyValuePair<FrontierKey, CacheEntry>>)_cache).Remove(new(key, entry));
            throw;
        }
        bool rootExact = false;
        for (int i = 0; i < moves.Count; i++)
        {
            if (!standings.TryGetValue(entry.Claims[i], out var standing)) continue;
            bonus[i] = Centipawns(standing);
            rootExact = true;
            Interlocked.Increment(ref _exactTransitionSignals);
        }
        if (rootExact) Interlocked.Increment(ref _rootsWithExactEvidence);
        return bonus;
    }

    /// <summary>The claim id of each move from <paramref name="root"/>, in the frontier's order.</summary>
    internal static Hash128[] Claims(Board root, Hash128 rootId, IReadOnlyList<ChessMove> moves)
    {
        var claims = new Hash128[moves.Count];
        for (int i = 0; i < moves.Count; i++)
        {
            var next = root.Clone();
            MoveApply.Make(next, moves[i]);
            claims[i] = ChessEngineIdentity.TransitionId(
                rootId, ChessEngineIdentity.MoveId(root, moves[i]), ChessEngineIdentity.PositionId(next));
        }
        return claims;
    }

    private Hash128[] Claims(Board root, Hash128 rootId, ChessMove[] moves)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            Interlocked.Increment(ref _frontierBuilds);
            return Claims(root, rootId, (IReadOnlyList<ChessMove>)moves);
        }
        finally { Interlocked.Add(ref _frontierTicks, Stopwatch.GetTimestamp() - started); }
    }

    private void TrimCache()
    {
        while ((_cache.Count > CacheCapacity || _cacheOrder.Count > CacheCapacity)
               && _cacheOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }

    /// <summary>
    /// A standing as a centipawn bonus: the conservative rating shrunk toward the neutral prior by the
    /// games folded into it, relative to that prior, capped.
    /// </summary>
    private int Centipawns(NpgsqlChessTransitionStandings.Row standing)
    {
        double effMu = standing.Confidence * 1e9, rd = standing.Deviation * 1e9;
        double shrunk = ChessShrink.ApplyExact(effMu, standing.Matches, rd, 0d, 0d, _shrinkK0);
        double pts = (shrunk - GlickoPriors.NeutralMu) / 1e9;
        return Math.Clamp((int)Math.Round(_cpPerPoint * pts), -_capCp, _capCp);
    }
}
