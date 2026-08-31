using System.Collections.Concurrent;
using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// One Laplace-native chess forward pass. Every legal transition is composed once, then its
/// position-transition, reusable move physicality (piece/from/to/special), and child-state
/// structure are read in three bounded batches and fused. There is no search or secondary
/// decision path hidden behind an unwitnessed position.
/// </summary>
public sealed class SubstrateTransitionChooser
{
    internal readonly record struct Rating(Hash128 Next, double EffMu, double Rd, double Witnesses);
    public readonly record struct Decision(
        ChessMove Move, bool ExactTransition, bool MovePhysicality, bool ChildStructure,
        bool TerminalPhysicality, double EffMu, double Rd, double Witnesses, long SubstrateEpoch)
    {
        public bool Rated => ExactTransition || MovePhysicality || ChildStructure || TerminalPhysicality;
    }
    public readonly record struct Statistics(
        long TrunkReads, long Decisions, long ExactTransitionSignals,
        long MovePhysicalitySignals, long ChildStructureSignals, long SubstrateEpoch);

    private readonly NpgsqlDataSource? _ds;
    private readonly Func<Hash128, int, IReadOnlyList<Rating>>? _read;
    private readonly Func<IReadOnlyList<Hash128>, IReadOnlyDictionary<Hash128, Rating>>? _readMoveOutcomes;
    private readonly Func<IReadOnlyList<string>, double[]>? _valueStates;
    private readonly SubstrateStateValuer? _stateValuer;
    private sealed record CacheEntry(long Version, Lazy<IReadOnlyList<Rating>> Rows);
    private readonly ConcurrentDictionary<Hash128, CacheEntry> _cache = new();
    private readonly ConcurrentQueue<Hash128> _insertionOrder = new();
    private readonly int _capacity;
    private readonly ChessModality _modality = new();
    private long _trunkReads;
    private long _decisions;
    private long _exactTransitionSignals;
    private long _movePhysicalitySignals;
    private long _childStructureSignals;

    public Statistics Snapshot => new(
        Volatile.Read(ref _trunkReads),
        Volatile.Read(ref _decisions),
        Volatile.Read(ref _exactTransitionSignals),
        Volatile.Read(ref _movePhysicalitySignals),
        Volatile.Read(ref _childStructureSignals),
        ChessTransitionObservations.Epoch);

    public SubstrateTransitionChooser(NpgsqlDataSource ds, int capacity = 16_384)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _stateValuer = new SubstrateStateValuer(ds);
        _capacity = Math.Max(256, capacity);
    }

    internal SubstrateTransitionChooser(
        Func<Hash128, int, IReadOnlyList<Rating>> read,
        Func<IReadOnlyList<Hash128>, IReadOnlyDictionary<Hash128, Rating>>? readMoveOutcomes = null,
        Func<IReadOnlyList<string>, double[]>? valueStates = null,
        int capacity = 16_384)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _readMoveOutcomes = readMoveOutcomes;
        _valueStates = valueStates;
        _capacity = Math.Max(16, capacity);
    }

    public MoveChooser CreateChooser(CancellationToken ct = default) =>
        (state, rng) => ChooseDecision(state, rng, ct).Move;

    internal ChessMove Choose(ChessState state, Random rng) => ChooseDecision(state, rng).Move;

    public Decision ChooseDecision(
        ChessState state, Random rng, CancellationToken ct = default)
    {
        var legal = _modality.LegalActions(state);
        if (legal.Count == 0)
            throw new InvalidOperationException("cannot choose a move from a terminal position");

        Hash128 rootId;
        lock (ChessCompose.Gate) rootId = ChessCompose.PositionId(state.Board);
        var candidates = ComposeCandidates(state, legal, rootId);
        var transitionRatings = Ratings(rootId, legal.Count, ct).ToDictionary(static r => r.Next);
        var moveRatings = ReadMoveOutcomes(candidates.Select(static c => c.MoveId).ToArray(), ct);
        var stateValues = ValueStates(candidates.Select(c => _modality.StateKey(c.Next)).ToArray(), ct);
        if (stateValues.Length != candidates.Count)
            throw new InvalidOperationException(
                $"substrate state valuer returned {stateValues.Length} values for {candidates.Count} legal transitions");

        int mover = _modality.SideToMove(state);
        CandidateScore? selected = null;
        int ties = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            double weightedScore = 0d, totalWeight = 0d, weightedRd = 0d;
            double witnesses = 0;
            bool exact = false, move = false, child = false, terminal = false;

            if (transitionRatings.TryGetValue(candidate.NextId, out var transition))
            {
                // chess.moves() exposes display units; the other substrate ratings are fp1e9.
                AddSignal(transition.EffMu * 1e9, transition.Rd * 1e9, transition.Witnesses,
                    ref weightedScore, ref totalWeight, ref weightedRd);
                witnesses += transition.Witnesses;
                exact = true;
            }
            if (moveRatings.TryGetValue(candidate.MoveId, out var moveRating))
            {
                AddSignal(moveRating.EffMu, moveRating.Rd, moveRating.Witnesses,
                    ref weightedScore, ref totalWeight, ref weightedRd);
                witnesses += moveRating.Witnesses;
                move = true;
            }
            if (stateValues[i] != GlickoPriors.NeutralMu)
            {
                // The child is valued for its side to move; reflect it to this mover.
                weightedScore += 2d * GlickoPriors.NeutralMu - stateValues[i];
                totalWeight += 1d;
                child = true;
            }
            if (_modality.Terminal(candidate.Next) is { } outcome)
            {
                // Terminality is a deterministic property of the composed transition.
                weightedScore = outcome.ForMover(mover) switch
                {
                    PlyOutcome.Win => 4_000_000_000_000d,
                    PlyOutcome.Loss => 100_000_000_000d,
                    _ => GlickoPriors.NeutralMu,
                };
                totalWeight = 1d;
                weightedRd = 0d;
                terminal = true;
            }
            if (totalWeight == 0d) continue;

            var scored = new CandidateScore(candidate, weightedScore / totalWeight,
                weightedRd / totalWeight, witnesses, exact, move, child, terminal);
            if (selected is null || scored.Score > selected.Score ||
                (scored.Score == selected.Score && scored.Witnesses > selected.Witnesses))
            {
                selected = scored;
                ties = 1;
            }
            else if (scored.Score == selected.Score && scored.Witnesses == selected.Witnesses
                     && rng.Next(++ties) == 0)
                selected = scored;
        }

        if (selected is not { } best)
            throw new UnratedSubstratePositionException(rootId, candidates.Count);

        Interlocked.Increment(ref _decisions);
        if (best.Exact) Interlocked.Increment(ref _exactTransitionSignals);
        if (best.Move) Interlocked.Increment(ref _movePhysicalitySignals);
        if (best.Child) Interlocked.Increment(ref _childStructureSignals);
        return new Decision(best.Candidate.Move, best.Exact, best.Move, best.Child, best.Terminal,
            best.Score / 1e9, best.Rd / 1e9, best.Witnesses, ChessTransitionObservations.Epoch);
    }

    private sealed record Candidate(ChessMove Move, Hash128 MoveId, Hash128 NextId, ChessState Next);
    private sealed record CandidateScore(
        Candidate Candidate, double Score, double Rd, double Witnesses,
        bool Exact, bool Move, bool Child, bool Terminal);

    private static void AddSignal(
        double score, double rd, double witnesses,
        ref double weightedScore, ref double totalWeight, ref double weightedRd)
    {
        double confidence = GlickoPriors.InitialRd /
                            (GlickoPriors.InitialRd + Math.Max(0d, rd));
        double weight = Math.Sqrt(Math.Max(1d, witnesses)) * confidence;
        weightedScore += score * weight;
        weightedRd += rd * weight;
        totalWeight += weight;
    }

    private IReadOnlyList<Candidate> ComposeCandidates(
        ChessState state, IReadOnlyList<ChessMove> legal, Hash128 rootId)
    {
        var candidates = new Candidate[legal.Count];
        lock (ChessCompose.Gate)
        {
            for (int i = 0; i < legal.Count; i++)
            {
                var move = legal[i];
                Piece moving = state.Board.Squares[move.From];
                Hash128 moveId = ChessCompose.MoveId(moving, move);
                Hash128 transitionKey = ChessCompose.TransitionKey(rootId, moveId);
                var next = _modality.Apply(state, move);
                if (!ChessTransitionFloor.TryLookup(transitionKey, out var nextId))
                {
                    nextId = ChessCompose.PositionId(next.Board);
                    ChessTransitionFloor.Remember(transitionKey, nextId);
                }
                candidates[i] = new Candidate(move, moveId, nextId, next);
            }
        }
        return candidates;
    }

    private IReadOnlyDictionary<Hash128, Rating> ReadMoveOutcomes(
        IReadOnlyList<Hash128> moveIds, CancellationToken ct)
    {
        if (_readMoveOutcomes is not null) return _readMoveOutcomes(moveIds);
        if (_ds is null) return new Dictionary<Hash128, Rating>();
        var edges = moveIds.ToDictionary(
            static id => id,
            static id => ConsensusKeys.EdgeId(
                id, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject));
        var rows = NpgsqlConsensusByIds.ReadAsync(
                _ds, edges.Values, ChessVocabulary.OutcomeType, ct)
            .GetAwaiter().GetResult();
        return edges.Where(kv => rows.ContainsKey(kv.Value)).ToDictionary(
            static kv => kv.Key,
            kv =>
            {
                var row = rows[kv.Value];
                return new Rating(kv.Key, row.EffMu, row.Rd, row.Witnesses);
            });
    }

    private double[] ValueStates(IReadOnlyList<string> surfaces, CancellationToken ct)
        => _valueStates is not null
            ? _valueStates(surfaces)
            : _stateValuer?.ValueStatesAsync(surfaces, ct).GetAwaiter().GetResult()
              ?? Enumerable.Repeat(GlickoPriors.NeutralMu, surfaces.Count).ToArray();

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

public sealed class UnratedSubstratePositionException : InvalidOperationException
{
    public UnratedSubstratePositionException(Hash128 position, int legalMoves)
        : base($"Laplace has no position-transition, move-physicality, or child-structure evidence for position {position} ({legalMoves} legal moves).")
    { }
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
