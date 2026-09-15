using global::Npgsql;
using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

public sealed class SubstrateRootBias : IRootBias
{
    private sealed record Evidence(
        IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Transitions,
        IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Moves);
    // Equality retains the complete ordered legal frontier, including flags/promotion.
    // A position-only cache can reuse evidence for a different filtered frontier.
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
    private sealed record Frontier(Hash128[] MoveIds, Hash128[] TransitionEdges, Hash128[] MoveOutcomeEdges);
    private sealed record CacheEntry(long Version, long ExpiresAt, Lazy<Frontier> Frontier, Lazy<Evidence> Value);

    private readonly double _cpPerPoint;
    private readonly int _capCp;
    private readonly double? _shrinkK0;
    private readonly Func<
        IReadOnlyCollection<Hash128>, Hash128,
        IReadOnlyCollection<Hash128>, Hash128,
        (IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> First,
         IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Second)> _read;
    private readonly Func<Hash128, IReadOnlyList<Hash128>, long> _version;
    private readonly ConcurrentDictionary<FrontierKey, CacheEntry> _cache = new();
    private readonly ConcurrentQueue<FrontierKey> _cacheOrder = new();
    private const int CacheCapacity = 16_384;
    private const long CacheLifetimeMilliseconds = 2_000;
    private readonly Func<long> _clock = static () => Environment.TickCount64;
    private long _frontierBuilds;
    private long _evidenceCacheHits;
    public long FrontierBuilds => Volatile.Read(ref _frontierBuilds);
    public long EvidenceCacheHits => Volatile.Read(ref _evidenceCacheHits);
    internal int CachedFrontiers => _cache.Count;
    internal int EvictionQueueCount => _cacheOrder.Count;
    private long _rootReads;
    private long _backendReads;
    private long _rootsWithExactEvidence;
    private long _rootsWithMoveEvidence;
    private long _exactTransitionSignals;
    private long _movePhysicalitySignals;
    private long _transitionPerfcacheHits;
    private long _transitionNovelHits;
    private long _transitionCompositions;

    public long RootReads => Volatile.Read(ref _rootReads);
    public long BackendReads => Volatile.Read(ref _backendReads);
    public long RootsWithExactEvidence => Volatile.Read(ref _rootsWithExactEvidence);
    public long RootsWithMoveEvidence => Volatile.Read(ref _rootsWithMoveEvidence);
    public long ExactTransitionSignals => Volatile.Read(ref _exactTransitionSignals);
    public long MovePhysicalitySignals => Volatile.Read(ref _movePhysicalitySignals);
    public long TransitionPerfcacheHits => Volatile.Read(ref _transitionPerfcacheHits);
    public long TransitionNovelHits => Volatile.Read(ref _transitionNovelHits);
    public long TransitionCompositions => Volatile.Read(ref _transitionCompositions);

    public SubstrateRootBias(NpgsqlDataSource ds, double cpPerPoint = 8.0, int capCp = 150, double? shrinkK0 = null)
    {
        ArgumentNullException.ThrowIfNull(ds);
        _cpPerPoint = cpPerPoint;
        _capCp = capCp;
        _shrinkK0 = shrinkK0;
        _version = ChessTransitionObservations.Version;
        _read = (firstIds, firstType, secondIds, secondType) =>
        {
            var pair = NpgsqlConsensusByIds.ReadPair(
                ds, firstIds, firstType, secondIds, secondType);
            return (pair.First, pair.Second);
        };
    }

    internal SubstrateRootBias(
        Func<
            IReadOnlyCollection<Hash128>, Hash128,
            IReadOnlyCollection<Hash128>, Hash128,
            (IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> First,
             IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Second)> read,
        double cpPerPoint = 8.0, int capCp = 150, double? shrinkK0 = null,
        Func<Hash128, IReadOnlyList<Hash128>, long>? version = null, Func<long>? clock = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _cpPerPoint = cpPerPoint;
        _capCp = capCp;
        _shrinkK0 = shrinkK0;
        _version = version ?? (static (_, _) => 0);
        _clock = clock ?? (static () => Environment.TickCount64);
    }

    public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
    {
        var bonus = new int[moves.Count];
        if (moves.Count == 0) return bonus;
        Interlocked.Increment(ref _rootReads);

        Hash128 rootId = ChessCompose.PositionId(root);
        var key = new FrontierKey(rootId, moves);
        CacheEntry entry;
        Lazy<Frontier>? derived = null;
        while (true)
        {
            _cache.TryGetValue(key, out var current);
            // Derivation is immutable for this exact root and ordered frontier. An
            // evidence refresh does not require repeating legal-transition composition.
            derived ??= current?.Frontier ?? new Lazy<Frontier>(
                () => BuildFrontier(root, key), LazyThreadSafetyMode.ExecutionAndPublication);
            var frontier = derived.Value;
            long version = _version(rootId, frontier.MoveIds);
            long now = _clock();
            if (current is not null && current.Version == version && current.ExpiresAt > now)
            {
                entry = current;
                Interlocked.Increment(ref _evidenceCacheHits);
                break;
            }
            var replacement = new CacheEntry(version, now + CacheLifetimeMilliseconds, derived,
                new Lazy<Evidence>(() =>
                {
                    Interlocked.Increment(ref _backendReads);
                    var pair = _read(frontier.TransitionEdges, ChessVocabulary.MoveType,
                        frontier.MoveOutcomeEdges, ChessVocabulary.OutcomeType);
                    return new Evidence(pair.First, pair.Second);
                }, LazyThreadSafetyMode.ExecutionAndPublication));
            bool installed = current is null
                ? _cache.TryAdd(key, replacement)
                : _cache.TryUpdate(key, replacement, current);
            if (!installed) continue;
            entry = replacement;
            // Replacing an expired entry must not grow an otherwise undrained queue.
            if (current is null) _cacheOrder.Enqueue(key);
            break;
        }
        TrimCache();
        Evidence evidence;
        try { evidence = entry.Value.Value; }
        catch
        {
            // Remove only this failed observation. A newer evidence generation may
            // already have replaced it while the database operation was in flight.
            ((ICollection<KeyValuePair<FrontierKey, CacheEntry>>)_cache).Remove(new(key, entry));
            throw;
        }
        var identities = entry.Frontier.Value;
        var transitionEdgeIds = identities.TransitionEdges;
        var moveOutcomeEdgeIds = identities.MoveOutcomeEdges;
        var transitions = evidence.Transitions;
        var moveOutcomes = evidence.Moves;
        bool rootExact = false, rootMove = false;
        for (int i = 0; i < moves.Count; i++)
        {
            double weightedDeviation = 0d, totalWeight = 0d;
            if (transitions.TryGetValue(transitionEdgeIds[i], out var exact))
            {
                AddSignal(exact, ref weightedDeviation, ref totalWeight);
                rootExact = true;
                Interlocked.Increment(ref _exactTransitionSignals);
            }
            if (moveOutcomes.TryGetValue(moveOutcomeEdgeIds[i], out var move))
            {
                AddSignal(move, ref weightedDeviation, ref totalWeight);
                rootMove = true;
                Interlocked.Increment(ref _movePhysicalitySignals);
            }
            if (totalWeight == 0d) continue;
            double pts = weightedDeviation / totalWeight / 1e9;
            bonus[i] = Math.Clamp((int)Math.Round(_cpPerPoint * pts), -_capCp, _capCp);
        }
        if (rootExact) Interlocked.Increment(ref _rootsWithExactEvidence);
        if (rootMove) Interlocked.Increment(ref _rootsWithMoveEvidence);
        return bonus;
    }

    private Frontier BuildFrontier(Board root, FrontierKey key)
    {
        Interlocked.Increment(ref _frontierBuilds);
        var moveIds = new Hash128[key.Moves.Length];
        var transitions = new Hash128[key.Moves.Length];
        var outcomes = new Hash128[key.Moves.Length];
        for (int i = 0; i < key.Moves.Length; i++)
        {
            ChessMove move = key.Moves[i];
            Hash128 moveId = ChessCompose.MoveId(root.Squares[move.From], move);
            moveIds[i] = moveId;
            Hash128 transitionKey = ChessCompose.TransitionKey(key.Root, moveId);
            Hash128 toId;
            if (ChessTransitionFloor.TryLookup(transitionKey, out toId, out var source))
            {
                if (source == ChessTransitionFloor.LookupSource.Persistent)
                    Interlocked.Increment(ref _transitionPerfcacheHits);
                else
                    Interlocked.Increment(ref _transitionNovelHits);
            }
            else
            {
                var next = root.Clone();
                MoveApply.Make(next, move);
                toId = ChessCompose.PositionId(next);
                ChessTransitionFloor.Remember(transitionKey, toId);
                Interlocked.Increment(ref _transitionCompositions);
            }
            transitions[i] = ConsensusKeys.EdgeId(key.Root, ChessVocabulary.MoveType, toId);
            outcomes[i] = ConsensusKeys.EdgeId(moveId, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject);
        }
        return new Frontier(moveIds, transitions, outcomes);
    }

    private void TrimCache()
    {
        while ((_cache.Count > CacheCapacity || _cacheOrder.Count > CacheCapacity)
               && _cacheOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }

    private void AddSignal(
        NpgsqlConsensusByIds.Row row, ref double weightedDeviation, ref double totalWeight)
    {
        double confidence = GlickoPriors.InitialRd /
                            (GlickoPriors.InitialRd + Math.Max(0d, row.Rd));
        double weight = Math.Sqrt(Math.Max(1d, row.Witnesses)) * confidence;
        double shrunk = ChessShrink.Apply(row.EffMu, row.Witnesses, _shrinkK0);
        weightedDeviation += (shrunk - GlickoPriors.NeutralMu) * weight;
        totalWeight += weight;
    }
}
