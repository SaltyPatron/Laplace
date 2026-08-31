using System.Collections.Concurrent;
using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Laplace-native chess decision: compose the legal state transitions, perform one indexed
/// trunk read for the current position, and choose the strongest witnessed continuation.
/// It does not put a substrate bonus on an alpha-beta scan. Unknown positions use the supplied
/// legal fallback; known positions are O(legal transitions) locally plus one cached trunk read.
/// </summary>
public sealed class SubstrateTransitionChooser
{
    internal readonly record struct Rating(Hash128 Next, double EffMu, double Rd, long Witnesses);
    public readonly record struct Decision(
        ChessMove Move, bool Witnessed, double EffMu, double Rd, long Witnesses,
        long SubstrateEpoch);
    public readonly record struct Statistics(
        long TrunkReads, long WitnessedDecisions, long FallbackDecisions, long SubstrateEpoch);

    private readonly NpgsqlDataSource? _ds;
    private readonly Func<Hash128, int, IReadOnlyList<Rating>>? _read;
    private sealed record CacheEntry(long Version, Lazy<IReadOnlyList<Rating>> Rows);
    private readonly ConcurrentDictionary<Hash128, CacheEntry> _cache = new();
    private readonly ConcurrentQueue<Hash128> _insertionOrder = new();
    private readonly int _capacity;
    private readonly ChessModality _modality = new();
    private long _trunkReads;
    private long _witnessedDecisions;
    private long _fallbackDecisions;

    public Statistics Snapshot => new(
        Volatile.Read(ref _trunkReads),
        Volatile.Read(ref _witnessedDecisions),
        Volatile.Read(ref _fallbackDecisions),
        ChessTransitionObservations.Epoch);

    public SubstrateTransitionChooser(NpgsqlDataSource ds, int capacity = 16_384)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _capacity = Math.Max(256, capacity);
    }

    internal SubstrateTransitionChooser(
        Func<Hash128, int, IReadOnlyList<Rating>> read, int capacity = 16_384)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _capacity = Math.Max(16, capacity);
    }

    public MoveChooser CreateChooser(MoveChooser fallback, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return (state, rng) => ChooseDecision(state, rng, fallback, ct).Move;
    }

    internal ChessMove Choose(ChessState state, Random rng, MoveChooser fallback)
        => ChooseDecision(state, rng, fallback).Move;

    public Decision ChooseDecision(
        ChessState state, Random rng, MoveChooser fallback, CancellationToken ct = default)
    {
        var legal = _modality.LegalActions(state);
        if (legal.Count == 0)
            throw new InvalidOperationException("cannot choose a move from a terminal position");

        Hash128 rootId;
        lock (ChessCompose.Gate) rootId = ChessCompose.PositionId(state.Board);
        var ratings = Ratings(rootId, legal.Count, ct);
        if (ratings.Count == 0)
        {
            Interlocked.Increment(ref _fallbackDecisions);
            return new Decision(fallback(state, rng), false, 0, 0, 0,
                ChessTransitionObservations.Epoch);
        }

        var byNext = ratings.ToDictionary(static r => r.Next);
        ChessMove? best = null;
        double bestMu = double.NegativeInfinity;
        double bestRd = 0;
        long bestWitnesses = -1;
        int ties = 0;

        lock (ChessCompose.Gate)
        {
            foreach (var move in legal)
            {
                Piece moving = state.Board.Squares[move.From];
                Hash128 moveId = ChessCompose.MoveId(moving, move);
                Hash128 transitionKey = ChessCompose.TransitionKey(rootId, moveId);
                Hash128 nextId;
                if (!ChessTransitionFloor.TryLookup(transitionKey, out nextId))
                {
                    var next = _modality.Apply(state, move);
                    nextId = ChessCompose.PositionId(next.Board);
                    ChessTransitionFloor.Remember(transitionKey, nextId);
                }
                if (!byNext.TryGetValue(nextId, out var rating)) continue;
                if (rating.EffMu > bestMu ||
                    (rating.EffMu == bestMu && rating.Witnesses > bestWitnesses))
                {
                    best = move;
                    bestMu = rating.EffMu;
                    bestRd = rating.Rd;
                    bestWitnesses = rating.Witnesses;
                    ties = 1;
                }
                else if (rating.EffMu == bestMu && rating.Witnesses == bestWitnesses
                         && rng.Next(++ties) == 0)
                {
                    best = move;
                    bestRd = rating.Rd;
                }
            }
        }
        if (best is { } selected)
        {
            Interlocked.Increment(ref _witnessedDecisions);
            return new Decision(selected, true, bestMu, bestRd, bestWitnesses,
                ChessTransitionObservations.Epoch);
        }
        Interlocked.Increment(ref _fallbackDecisions);
        return new Decision(fallback(state, rng), false, 0, 0, 0,
            ChessTransitionObservations.Epoch);
    }

    private IReadOnlyList<Rating> Ratings(Hash128 rootId, int limit, CancellationToken ct)
    {
        long version = ChessTransitionObservations.Version(rootId);
        CacheEntry entry;
        while (true)
        {
            if (_cache.TryGetValue(rootId, out var current) && current.Version == version)
            {
                entry = current;
                break;
            }
            var replacement = new CacheEntry(version,
                new Lazy<IReadOnlyList<Rating>>(
                    () => Read(rootId, limit, ct), LazyThreadSafetyMode.ExecutionAndPublication));
            bool installed = current is null
                ? _cache.TryAdd(rootId, replacement)
                : _cache.TryUpdate(rootId, replacement, current);
            if (installed)
            {
                entry = replacement;
                _insertionOrder.Enqueue(rootId);
                break;
            }
        }
        Trim();
        try { return entry.Rows.Value; }
        catch
        {
            if (_cache.TryGetValue(rootId, out var current) && ReferenceEquals(current, entry))
                _cache.TryRemove(rootId, out _);
            throw;
        }
    }

    private IReadOnlyList<Rating> Read(Hash128 rootId, int limit, CancellationToken ct)
    {
        Interlocked.Increment(ref _trunkReads);
        if (_read is not null) return _read(rootId, limit);
        var rows = NpgsqlSubstrateReads.ChessMovesAsync(
                _ds!, rootId.ToBytes(), limit, ct)
            .GetAwaiter().GetResult();
        return rows.Select(static r => new Rating(
            Hash128.FromBytes(r.NextPosition), r.EffMu, r.Rd, r.WitnessCount)).ToArray();
    }

    private void Trim()
    {
        while (_cache.Count > _capacity && _insertionOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }
}

/// <summary>
/// Process-local invalidation index for the persistent transition fold. A completed playing
/// advances only the position trunks it actually witnessed, so four concurrent games share
/// hot reads while a later game immediately sees new evidence from an earlier completion.
/// </summary>
internal static class ChessTransitionObservations
{
    private static readonly ConcurrentDictionary<Hash128, long> Versions = new();
    private static long _epoch;

    public static long Epoch => Volatile.Read(ref _epoch);
    public static long Version(Hash128 position) =>
        Versions.TryGetValue(position, out long version) ? version : 0;

    public static void MarkObserved(IEnumerable<Hash128> positions)
    {
        long version = Interlocked.Increment(ref _epoch);
        foreach (var position in positions)
            Versions.AddOrUpdate(position, version, (_, _) => version);
    }
}
