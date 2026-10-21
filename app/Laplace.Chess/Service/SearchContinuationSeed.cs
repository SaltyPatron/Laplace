using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Builds the per-search continuation cache outside the search. The active line and
/// one ply of legal successors are local, augmented by local retrograde checkmate and
/// &gt;5-man sub-board Syzygy projection plus a multi-ply witnessed trajectory horizon
/// and <c>HAS_EVAL</c> sums when a substrate connection is open.
/// </summary>
public static class SearchContinuationSeed
{
    public const int DefaultHorizonPlies = 6;
    public const int DefaultHorizonLines = 16;

    public static SearchContinuationTable ForUci(
        ChessModality modality,
        string? fen,
        IReadOnlyList<string>? uciMoves,
        NpgsqlDataSource? witnessed,
        ISyzygyProber? syzygyProber = null)
    {
        ArgumentNullException.ThrowIfNull(modality);
        var start = string.IsNullOrWhiteSpace(fen) ? modality.Initial() : modality.FromFen(fen);
        var origin = start.Board.Clone();
        var cursor = origin.Clone();
        var line = new List<ChessMove>();
        var positions = new List<Board> { cursor.Clone() };
        if (uciMoves is not null)
        {
            foreach (var uci in uciMoves)
            {
                ChessMove? found = null;
                foreach (var candidate in MoveGen.Legal(cursor))
                {
                    if (candidate.ToUci() != uci) continue;
                    found = candidate;
                    break;
                }
                if (found is null) break;
                line.Add(found.Value);
                MoveApply.Make(cursor, found.Value);
                positions.Add(cursor.Clone());
            }
        }

        var table = new SearchContinuationTable(OpeningContinuationBook.Shared);
        var current = positions[^1];
        var prober = syzygyProber ?? ChessTablebaseRuntime.Prober;
        int maxTbMen = prober is null ? 0 : Math.Min(ChessSyzygy.WitnessedMenCeiling, prober.Largest);

        foreach (var move in MoveGen.Legal(current))
        {
            var next = current.Clone();
            MoveApply.Make(next, move);
            positions.Add(next);

            var nextLegal = MoveGen.Legal(next);
            if (nextLegal.Count == 0 && MoveGen.InCheck(next, next.WhiteToMove))
            {
                table.SeedRetrogradeCheckpoint(next, null, wdl: 0, pliesToClose: 0, isCheckmate: true);
                table.SeedRetrogradeCheckpoint(current, move, wdl: 4, pliesToClose: 1, isCheckmate: true);
                continue;
            }

            if (prober is not null && next.Rules.IsStandard && next.Castle == CastleRights.None)
            {
                int men = ChessSyzygy.MenCount(next);
                if (men == maxTbMen + 1
                    && ChessRetrograde.ProbeOnePlySyzygyClosure(next, prober, maxTbMen) is { } closure)
                {
                    table.SeedRetrogradeCheckpoint(
                        next, closure.Move, closure.Wdl, closure.PliesToClose, isCheckmate: false);
                }
                else if (men > maxTbMen && men <= 10
                         && ChessRetrograde.ProjectSubBoardForSyzygy(next, prober, maxTbMen) is { } subProj
                         && subProj.Wdl is 0 or 4)
                {
                    int subCp = subProj.Wdl == 4 ? 220 : -220;
                    table.SeedWitnessedEvaluation(next, subCp, SearchContinuationTable.BlendPrior);
                }
            }
        }

        if (line.Count > 0)
            table.SeedActiveGame(origin, line);
        if (witnessed is not null)
            SeedWitnessed(table, current, positions, witnessed);
        return table;
    }

    /// <summary>
    /// Inverse of <see cref="PgnEvals.EvalSumFp1e9"/>. Null when the stored probability
    /// is outside the open unit interval.
    /// </summary>
    public static int? Centipawns(long sumScoreFp1e9, long observations)
    {
        if (observations <= 0) return null;
        double p = sumScoreFp1e9 / (observations * 1_000_000_000d);
        if (p <= 1e-6 || p >= 1d - 1e-6) return null;
        double cp = -400d * Math.Log((1d - p) / p);
        if (double.IsNaN(cp) || double.IsInfinity(cp)) return null;
        return (int)Math.Round(Math.Clamp(cp, -20_000d, 20_000d));
    }

    /// <summary>
    /// Expands a set of witnessed <c>(FromPosition, ToPosition)</c> trajectory steps
    /// starting from <paramref name="root"/>, seeding preferred continuation moves into
    /// <paramref name="table"/> and adding reachable horizon boards to <paramref name="boards"/>.
    /// </summary>
    public static int ApplyHorizonSteps(
        SearchContinuationTable table,
        Board root,
        IDictionary<Hash128, Board> boards,
        IReadOnlyList<NpgsqlSubstrateReads.HorizonStepRow> steps,
        int maxPlies = DefaultHorizonPlies)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(boards);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0 || maxPlies < 1) return 0;

        Hash128 rootId = ChessPositionIdentity.PositionId(root);
        boards.TryAdd(rootId, root);

        var stepMap = new Dictionary<Hash128, List<Hash128>>();
        foreach (var row in steps)
        {
            var fromId = Hash128.FromBytes(row.FromPosition);
            var toId = Hash128.FromBytes(row.ToPosition);
            if (!stepMap.TryGetValue(fromId, out var list))
            {
                list = new List<Hash128>(4);
                stepMap[fromId] = list;
            }
            list.Add(toId);
        }

        int seeded = 0;
        var frontier = new HashSet<Hash128> { rootId };
        for (int ply = 0; ply < maxPlies && frontier.Count > 0; ply++)
        {
            var nextFrontier = new HashSet<Hash128>();
            foreach (var fromId in frontier)
            {
                if (!boards.TryGetValue(fromId, out var fromBoard)) continue;
                if (!stepMap.TryGetValue(fromId, out var targets)) continue;

                var legal = MoveGen.Legal(fromBoard);
                bool firstSeededForFrom = false;
                foreach (var toId in targets)
                {
                    foreach (var move in legal)
                    {
                        var child = fromBoard.Clone();
                        MoveApply.Make(child, move);
                        if (ChessPositionIdentity.PositionId(child) != toId) continue;

                        if (!firstSeededForFrom)
                        {
                            table.SeedOpening(fromBoard, [move]);
                            firstSeededForFrom = true;
                            seeded++;
                        }
                        if (boards.TryAdd(toId, child))
                            nextFrontier.Add(toId);
                        break;
                    }
                }
            }
            frontier = nextFrontier;
        }
        return seeded;
    }

    private static void SeedWitnessed(
        SearchContinuationTable table,
        Board current,
        List<Board> positions,
        NpgsqlDataSource dataSource)
    {
        var boards = new Dictionary<Hash128, Board>();
        foreach (var board in positions)
            boards.TryAdd(ChessPositionIdentity.PositionId(board), board);

        try
        {
            var horizonSteps = NpgsqlSubstrateReads.MultiPlyHorizonSteps(
                dataSource,
                ChessPositionIdentity.PositionId(current).ToBytes(),
                ChessVocabulary.GameType.ToBytes(),
                (short)PhysicalityType.Projection,
                DefaultHorizonPlies,
                DefaultHorizonLines);
            if (horizonSteps.Count > 0)
                ApplyHorizonSteps(table, current, boards, horizonSteps, DefaultHorizonPlies);
        }
        catch (Exception failure)
        {
            // Horizon trajectory expansion is an optimization; never fail search setup.
            System.Diagnostics.Trace.TraceWarning(
                "CHESS_HORIZON_READ_FAILED position={0}; continuing with local continuations: {1}",
                ChessPositionIdentity.PositionId(current), failure);
        }

        var raw = new byte[boards.Count][];
        int index = 0;
        foreach (var id in boards.Keys)
            raw[index++] = id.ToBytes();

        var rows = NpgsqlSubstrateReads.HasEvalSums(
            dataSource,
            ChessVocabulary.HasEvalType.ToBytes(),
            ChessVocabulary.HasEvalObject.ToBytes(),
            raw);
        var byId = new Dictionary<Hash128, NpgsqlSubstrateReads.EvalSumRow>(rows.Count);
        foreach (var row in rows)
            byId[Hash128.FromBytes(row.Position)] = row;

        foreach (var (id, board) in boards)
        {
            if (!byId.TryGetValue(id, out var row)) continue;
            if (Centipawns(row.SumScoreFp1e9, row.Observations) is not int cp) continue;
            table.SeedWitnessedEvaluation(
                board,
                cp,
                row.Observations,
                amputateInterior: row.Observations >= SearchContinuationTable.AmputationWitnesses);
        }
    }
}
