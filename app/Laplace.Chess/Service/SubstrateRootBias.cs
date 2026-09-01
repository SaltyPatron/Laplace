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
    private sealed record CacheEntry(long Version, long ExpiresAt, Lazy<Evidence> Value);

    private readonly ChessModality _modality = new();
    private readonly double _cpPerPoint;
    private readonly int _capCp;
    private readonly double? _shrinkK0;
    private readonly Func<
        IReadOnlyCollection<Hash128>, Hash128,
        IReadOnlyCollection<Hash128>, Hash128,
        (IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> First,
         IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Second)> _read;
    private readonly Func<Hash128, IReadOnlyList<Hash128>, long> _version;
    private readonly ConcurrentDictionary<Hash128, CacheEntry> _cache = new();
    private readonly ConcurrentQueue<Hash128> _cacheOrder = new();
    private const int CacheCapacity = 16_384;
    private static readonly long CacheLifetimeTicks = TimeSpan.FromSeconds(2).Ticks;
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
        Func<Hash128, IReadOnlyList<Hash128>, long>? version = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _cpPerPoint = cpPerPoint;
        _capCp = capCp;
        _shrinkK0 = shrinkK0;
        _version = version ?? (static (_, _) => 0);
    }

    public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
    {
        var bonus = new int[moves.Count];
        if (moves.Count == 0) return bonus;
        Interlocked.Increment(ref _rootReads);

        var state = _modality.FromFen(root.ToFen());
        var transitionEdgeIds = new Hash128[moves.Count];
        var moveIds = new Hash128[moves.Count];
        var moveOutcomeEdgeIds = new Hash128[moves.Count];
        Hash128 rootId;
        lock (ChessCompose.Gate)
        {
            rootId = ChessCompose.PositionId(state.Board);
            for (int i = 0; i < moves.Count; i++)
            {
                Piece moving = state.Board.Squares[moves[i].From];
                Hash128 moveId = ChessCompose.MoveId(moving, moves[i]);
                moveIds[i] = moveId;
                Hash128 transitionKey = ChessCompose.TransitionKey(rootId, moveId);
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
                    var next = _modality.Apply(state, moves[i]);
                    toId = ChessCompose.PositionId(next.Board);
                    ChessTransitionFloor.Remember(transitionKey, toId);
                    Interlocked.Increment(ref _transitionCompositions);
                }
                transitionEdgeIds[i] = ConsensusKeys.EdgeId(
                    rootId, ChessVocabulary.MoveType, toId);
                moveOutcomeEdgeIds[i] = ConsensusKeys.EdgeId(
                    moveId, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject);
            }
        }

        // One database command materializes this exact legal frontier. The result is reusable
        // until a completed game changes this position or one of these typed moves. A short
        // wall-clock expiry also observes ingest performed by another process, whose local
        // observation generation cannot be seen here.
        long version = _version(rootId, moveIds);
        long now = DateTime.UtcNow.Ticks;
        CacheEntry entry;
        while (true)
        {
            if (_cache.TryGetValue(rootId, out var current)
                && current.Version == version && current.ExpiresAt > now)
            {
                entry = current;
                break;
            }
            var replacement = new CacheEntry(version, now + CacheLifetimeTicks,
                new Lazy<Evidence>(() =>
                {
                    Interlocked.Increment(ref _backendReads);
                    var pair = _read(
                        transitionEdgeIds, ChessVocabulary.MoveType,
                        moveOutcomeEdgeIds, ChessVocabulary.OutcomeType);
                    return new Evidence(pair.First, pair.Second);
                }, LazyThreadSafetyMode.ExecutionAndPublication));
            bool installed = current is null
                ? _cache.TryAdd(rootId, replacement)
                : _cache.TryUpdate(rootId, replacement, current);
            if (!installed) continue;
            entry = replacement;
            _cacheOrder.Enqueue(rootId);
            break;
        }
        TrimCache();
        Evidence evidence;
        try { evidence = entry.Value.Value; }
        catch
        {
            if (_cache.TryGetValue(rootId, out var current) && ReferenceEquals(current, entry))
                _cache.TryRemove(rootId, out _);
            throw;
        }
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

    private void TrimCache()
    {
        while (_cache.Count > CacheCapacity && _cacheOrder.TryDequeue(out var oldest))
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
