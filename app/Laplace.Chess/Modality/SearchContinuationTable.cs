using Laplace.Engine.Core;

namespace Laplace.Modality.Chess;

/// <summary>
/// Cache of witnessed continuations for one search. Keys are
/// <see cref="ChessPositionIdentity"/> values, the same position identity the
/// game already uses. The table does not mint a second identity.
/// Seed it before <see cref="Search.Think"/> and do not mutate it during a search.
/// Search uses <see cref="Entry.PreferredMove"/> to order interior nodes.
/// <see cref="Entry.WitnessedEvaluationCp"/> is stored for the leaf blend and is
/// not added again at every ply.
/// </summary>
public sealed class SearchContinuationTable
{
    /// <summary>Largest witnessed-evaluation nudge a leaf blend may apply.</summary>
    public const int BlendCapCp = 512;

    /// <summary>
    /// Prior strength of one witnessed evaluation. Five observations and this prior
    /// keep two thirds of the gap, the same finite-sample law as an exact transition.
    /// </summary>
    public const double BlendPrior = 5d;

    /// <summary>
    /// Witness count at which a horizon-settled node retains at least 75% of its
    /// empirical standing and can amputate remaining interior depth to quiescence.
    /// </summary>
    public const double AmputationWitnesses = 15d;

    public readonly record struct Entry(
        ChessMove? ActiveGameMove,
        ChessMove? OpeningMove,
        int? WitnessedEvaluationCp,
        double WitnessedEvaluationWitnesses = 0d,
        ChessMove? RetrogradeMove = null,
        int? RetrogradeWdl = null,
        int? RetrogradePlies = null,
        bool RetrogradeCheckmate = false,
        bool AmputateInterior = false)
    {
        /// <summary>The game being played outranks retrograde and opening moves.</summary>
        public ChessMove? PreferredMove => ActiveGameMove ?? RetrogradeMove ?? OpeningMove;

        /// <summary>True when backward unrolling resolved an exact mate or tablebase closure.</summary>
        public bool IsRetrogradeSolved => RetrogradeWdl is not null && RetrogradePlies is not null;

        /// <summary>True when consensus precision is high enough to amputate interior branching.</summary>
        public bool CanAmputateInterior =>
            AmputateInterior
            && WitnessedEvaluationCp is not null
            && WitnessedEvaluationWitnesses >= AmputationWitnesses;

        /// <summary>
        /// Precision-weighted step from a structural leaf toward the witnessed
        /// centipawn value. Zero when this position has no witnessed evaluation.
        /// </summary>
        public int LeafNudge(int structuralCp)
        {
            if (WitnessedEvaluationCp is not int witnessed || WitnessedEvaluationWitnesses <= 0d)
                return 0;
            double n = WitnessedEvaluationWitnesses;
            double w = n / (n + BlendPrior);
            int nudge = (int)Math.Round((witnessed - structuralCp) * w);
            if (nudge > BlendCapCp) return BlendCapCp;
            if (nudge < -BlendCapCp) return -BlendCapCp;
            return nudge;
        }
    }

    private readonly Dictionary<Hash128, Entry> _entries = new();
    private readonly SearchContinuationTable? _fallback;
    private int _evalStamp;

    public SearchContinuationTable(SearchContinuationTable? fallback = null) => _fallback = fallback;

    /// <summary>
    /// Changes when a witnessed evaluation is seeded. Move-only seeds leave it at zero,
    /// so replacing an ordering table does not discard transposition scores.
    /// </summary>
    public int EvaluationStamp => _evalStamp;

    public bool TryGet(Hash128 positionId, out Entry entry)
    {
        bool local = _entries.TryGetValue(positionId, out var mine);
        Entry parent = default;
        bool inherited = _fallback is not null && _fallback.TryGet(positionId, out parent);
        if (!local && !inherited)
        {
            entry = default;
            return false;
        }
        if (!inherited)
        {
            entry = mine;
            return true;
        }
        if (!local)
        {
            entry = parent;
            return true;
        }
        entry = new Entry(
            mine.ActiveGameMove ?? parent.ActiveGameMove,
            mine.OpeningMove ?? parent.OpeningMove,
            mine.WitnessedEvaluationCp ?? parent.WitnessedEvaluationCp,
            mine.WitnessedEvaluationWitnesses > 0d
                ? mine.WitnessedEvaluationWitnesses
                : parent.WitnessedEvaluationWitnesses,
            mine.RetrogradeMove ?? parent.RetrogradeMove,
            mine.RetrogradeWdl ?? parent.RetrogradeWdl,
            mine.RetrogradePlies ?? parent.RetrogradePlies,
            mine.RetrogradeCheckmate || parent.RetrogradeCheckmate,
            mine.AmputateInterior || parent.AmputateInterior);
        return true;
    }

    public void SeedActiveGame(Board start, IReadOnlyList<ChessMove> line)
        => SeedLine(start, line, activeGame: true);

    public void SeedOpening(Board start, IReadOnlyList<ChessMove> line)
        => SeedLine(start, line, activeGame: false);

    public void SeedWitnessedEvaluation(
        Board position, int centipawnsFromSideToMove, double witnesses = BlendPrior, bool amputateInterior = false)
    {
        Hash128 id = ChessPositionIdentity.PositionId(position);
        double n = witnesses > 0d ? witnesses : BlendPrior;
        Entry current = _entries.GetValueOrDefault(id);
        _entries[id] = current with
        {
            WitnessedEvaluationCp = centipawnsFromSideToMove,
            WitnessedEvaluationWitnesses = n,
            AmputateInterior = current.AmputateInterior || amputateInterior,
        };
        _evalStamp = HashCode.Combine(_evalStamp, id.GetHashCode(), centipawnsFromSideToMove, n, amputateInterior);
    }

    /// <summary>
    /// Seeds a horizon-settled checkpoint whose witness count is sufficient to amputate
    /// interior search subtrees directly to quiescence.
    /// </summary>
    public void SeedHorizonCheckpoint(Board position, int centipawnsFromSideToMove, double witnesses = AmputationWitnesses)
        => SeedWitnessedEvaluation(
            position,
            centipawnsFromSideToMove,
            Math.Max(AmputationWitnesses, witnesses),
            amputateInterior: true);

    /// <summary>
    /// Seeds a retrograde checkmate or tablebase-closure checkpoint (including on positions
    /// with more than five pieces). When multiple backward lines reach the same position,
    /// the higher WDL wins, and among equal winning WDLs the fastest closure (minimum plies)
    /// wins.
    /// </summary>
    public void SeedRetrogradeCheckpoint(
        Board position, ChessMove? bestMove, int wdl, int pliesToClose, bool isCheckmate = false)
    {
        Hash128 id = ChessPositionIdentity.PositionId(position);
        int clampedWdl = Math.Clamp(wdl, 0, 4);
        int clampedPlies = Math.Max(0, pliesToClose);
        Entry current = _entries.GetValueOrDefault(id);
        if (current.IsRetrogradeSolved)
        {
            int curWdl = current.RetrogradeWdl!.Value;
            int curPlies = current.RetrogradePlies!.Value;
            bool betterWdl = clampedWdl > curWdl;
            bool sameWdlBetterMate = clampedWdl == curWdl && isCheckmate && !current.RetrogradeCheckmate;
            bool sameKindFaster = clampedWdl == curWdl
                && isCheckmate == current.RetrogradeCheckmate
                && (clampedWdl >= 3 ? clampedPlies < curPlies : clampedPlies > curPlies);
            if (!betterWdl && !sameWdlBetterMate && !sameKindFaster)
                return;
        }
        _entries[id] = current with
        {
            RetrogradeMove = bestMove ?? current.RetrogradeMove,
            RetrogradeWdl = clampedWdl,
            RetrogradePlies = clampedPlies,
            RetrogradeCheckmate = isCheckmate,
        };
        _evalStamp = HashCode.Combine(_evalStamp, id.GetHashCode(), clampedWdl, clampedPlies, isCheckmate);
    }

    private void SeedLine(Board start, IReadOnlyList<ChessMove> line, bool activeGame)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(line);
        var board = start.Clone();
        foreach (var move in line)
        {
            Hash128 id = ChessPositionIdentity.PositionId(board);
            Entry current = _entries.GetValueOrDefault(id);
            if (activeGame)
                _entries[id] = current with { ActiveGameMove = move };
            else if (current.OpeningMove is null)
                _entries[id] = current with { OpeningMove = move };
            MoveApply.Make(board, move);
        }
    }
}
