using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Substrate-backed move commentary. The sentence is the opening transition, the
/// structural or tactical delta, the principal variation, and a master or book
/// precedent only when that precedent is salient. Motif names come from the chess
/// concept, so a fork stays in the chess domain.
/// </summary>
public static class ChessMoveCommentary
{
    public const int LichessMaxChars = 140;
    public const int MasterElo = 2400;

    public sealed record Inputs(
        int ScoreCp,
        int Depth,
        IReadOnlyList<string> Pv,
        IReadOnlyList<string> Motifs,
        string? PositionSurface = null,
        string? PlayedSan = null,
        IReadOnlyList<string>? UciMoves = null,
        string? StartFen = null);

    public static async Task<string> BuildAsync(
        NpgsqlDataSource ds, Inputs input, CancellationToken ct = default, int maxChars = LichessMaxChars)
    {
        var sans = SansFromUci(input.StartFen, input.UciMoves);
        var opening = OpeningClassifier.Classify(sans);
        string? openingLine = OpeningTransition(opening.Name, sans, input.PlayedSan);
        string? motif = input.Motifs.FirstOrDefault(static m =>
            ChessConcepts.TryResolve(m, chessDomain: true, out _));
        string? delta = motif is null ? null : MotifLabel(motif);

        var historyTask = input.PositionSurface is { } historySurface
            ? HistoricalPositionLineAsync(ds, historySurface, input.PlayedSan, opening.Eco, motif, ct)
            : Task.FromResult<string?>(null);
        var bookTask = input.PositionSurface is { } bookSurface
            ? BookLineAsync(ds, bookSurface, ct)
            : Task.FromResult<string?>(null);
        var populationTask = input.PositionSurface is { } populationSurface
            ? PopulationLineAsync(ds, populationSurface, ct)
            : Task.FromResult<string?>(null);
        await Task.WhenAll(historyTask, bookTask, populationTask).ConfigureAwait(false);

        return Compose(
            openingLine,
            delta,
            input.Pv,
            FormatEval(input.ScoreCp, input.Depth),
            historyTask.Result,
            bookTask.Result,
            populationTask.Result,
            maxChars);
    }

    internal static string Compose(
        string? opening,
        string? delta,
        IReadOnlyList<string> pv,
        string? evalLine,
        string? precedent,
        string? book,
        string? population,
        int maxChars)
    {
        var parts = new List<string>(7);
        if (!string.IsNullOrWhiteSpace(opening)) parts.Add(opening.Trim());
        if (!string.IsNullOrWhiteSpace(delta)) parts.Add(delta.Trim());
        if (pv.Count > 0) parts.Add("PV " + string.Join(' ', pv.Take(3)));
        if (!string.IsNullOrWhiteSpace(evalLine)) parts.Add(evalLine.Trim());
        if (!string.IsNullOrWhiteSpace(precedent)) parts.Add(precedent.Trim());
        if (!string.IsNullOrWhiteSpace(book)) parts.Add(book.Trim());
        if (!string.IsNullOrWhiteSpace(population)) parts.Add(population.Trim());
        return Truncate(string.Join(" · ", parts.Where(static p => p.Length > 0)), maxChars);
    }

    internal static bool IsSalientPlayer(double? effMu, int? elo) =>
        elo is >= MasterElo || effMu is >= GlickoPriors.NeutralMu;

    internal static bool IsSalientBook(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 12;

    /// <summary>
    /// Higher Glicko-2 lower bound, then higher source Elo, then an opening or motif
    /// match. A date is not a key.
    /// </summary>
    internal static int CompareSalience(
        double? effMu, int? elo, int openingOrMotif,
        double? otherEffMu, int? otherElo, int otherOpeningOrMotif)
    {
        int byEff = CompareDescending(effMu, otherEffMu);
        if (byEff != 0) return byEff;
        int byElo = CompareDescending(elo, otherElo);
        if (byElo != 0) return byElo;
        return otherOpeningOrMotif.CompareTo(openingOrMotif);
    }

    internal static string FormatDistribution(IReadOnlyList<(string San, long Count)> moves, long witnesses)
    {
        if (witnesses <= 0) return "";
        if (moves.Count == 0 || moves.Sum(static m => m.Count) < witnesses)
            return witnesses == 1
                ? "1 game reached this position"
                : witnesses.ToString(System.Globalization.CultureInfo.InvariantCulture) + " games reached this position";
        string body = string.Join(", ", moves.Take(4).Select(static m =>
            m.San + " " + m.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return "Next " + body;
    }

    private static int CompareDescending(double? left, double? right)
    {
        if (left is null && right is null) return 0;
        if (left is null) return 1;
        if (right is null) return -1;
        return right.Value.CompareTo(left.Value);
    }

    private static int CompareDescending(int? left, int? right)
    {
        if (left is null && right is null) return 0;
        if (left is null) return 1;
        if (right is null) return -1;
        return right.Value.CompareTo(left.Value);
    }

    private static string? OpeningTransition(string? name, IReadOnlyList<string> sans, string? playedSan)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string? transition = sans.Count > 0 ? sans[^1] : playedSan;
        return string.IsNullOrWhiteSpace(transition) ? name : name + ": " + transition;
    }

    private static List<string> SansFromUci(string? fen, IReadOnlyList<string>? uci)
    {
        var sans = new List<string>();
        if (uci is not { Count: > 0 }) return sans;
        var modality = new ChessModality();
        ChessState state;
        try
        {
            state = string.IsNullOrWhiteSpace(fen) ? modality.Initial() : modality.FromFen(fen);
        }
        catch (FormatException)
        {
            return sans;
        }
        foreach (var token in uci)
        {
            ChessMove? match = null;
            foreach (var legal in MoveGen.Legal(state.Board))
            {
                if (legal.ToUci() == token) { match = legal; break; }
            }
            if (match is null) break;
            sans.Add(San.ToSan(state.Board, match.Value));
            state = modality.Apply(state, match.Value);
        }
        return sans;
    }

    private static readonly Hash128 ExplainsRelation = RelationTypeRegistry.RelationTypeId("EXPLAINS");

    private static async Task<string?> HistoricalPositionLineAsync(
        NpgsqlDataSource ds, string positionSurface, string? playedSan, string? eco, string? motif,
        CancellationToken ct)
    {
        try
        {
            if (!PositionContent.TryFenFromSurface(positionSurface, out var fen)) return null;
            var board = Board.FromFen(fen);
            Hash128 positionId;
            lock (ChessCompose.Gate) { positionId = ChessCompose.PositionId(board); }

            var history = await NpgsqlChessCommentaryReads.PositionHistoryAsync(
                ds,
                positionId.ToBytes(),
                ChessVocabulary.GameType.ToBytes(),
                (short)PhysicalityType.Projection,
                containerLimit: 64,
                limit: 12,
                ct,
                whiteToMove: board.WhiteToMove,
                eco: eco,
                motif: motif).ConfigureAwait(false);
            if (history.Count == 0) return null;

            var ordered = history
                .OrderBy(static h => h, Comparer<NpgsqlChessCommentaryReads.PositionHistoryRow>.Create(
                    static (a, b) => CompareSalience(
                        a.MoverEffMu, a.MoverElo, a.OpeningOrMotif,
                        b.MoverEffMu, b.MoverElo, b.OpeningOrMotif)))
                .ToArray();

            var distinctLines = ordered
                .Select(static h => Hash128.FromBytes(h.LineId))
                .Distinct()
                .Select(static id => id.ToBytes())
                .ToArray();
            var projection = await NpgsqlSubstrateReads.TrajectoryConstituentsAsync(
                ds, distinctLines, PhysicalityType.Projection, ct).ConfigureAwait(false);

            var byLine = projection
                .GroupBy(static p => Hash128.FromBytes(p.ParentId))
                .ToDictionary(
                    static g => g.Key,
                    static g => g.OrderBy(static p => p.Ordinal)
                        .Select(static p => Hash128.FromBytes(p.EntityId)).ToArray());

            var nextMoves = LegalSuccessors(board);
            foreach (var row in ordered)
            {
                if (!IsSalientPlayer(row.MoverEffMu, row.MoverElo)) continue;
                string mover = board.WhiteToMove ? row.White : row.Black;
                if (string.IsNullOrWhiteSpace(mover)) continue;

                string? nextSan = null;
                var lineId = Hash128.FromBytes(row.LineId);
                if (byLine.TryGetValue(lineId, out var positions))
                {
                    for (int i = 0; i + 1 < positions.Length; i++)
                    {
                        if (positions[i] != positionId) continue;
                        if (nextMoves.TryGetValue(positions[i + 1], out var san))
                            nextSan = san;
                        break;
                    }
                }

                return FormatHistorical(row.PlayedOn, mover, nextSan, playedSan)
                    ?? FormatUndated(mover, nextSan, playedSan);
            }
        }
        catch
        {
            // Commentary is decoration; a failed historical lookup must never affect play.
        }
        return null;
    }

    private static async Task<string?> PopulationLineAsync(
        NpgsqlDataSource ds, string positionSurface, CancellationToken ct)
    {
        try
        {
            if (!PositionContent.TryFenFromSurface(positionSurface, out var fen)) return null;
            var board = Board.FromFen(fen);
            Hash128 positionId;
            lock (ChessCompose.Gate) { positionId = ChessCompose.PositionId(board); }
            var counts = await NpgsqlChessCommentaryReads.PositionContinuationsAsync(
                ds,
                positionId.ToBytes(),
                ChessVocabulary.GameType.ToBytes(),
                (short)PhysicalityType.Projection,
                limit: 8,
                ct).ConfigureAwait(false);
            if (counts.Count == 0) return null;
            var nextMoves = LegalSuccessors(board);
            var named = new List<(string San, long Count)>();
            long witnesses = 0;
            foreach (var row in counts)
            {
                witnesses += row.Count;
                if (nextMoves.TryGetValue(Hash128.FromBytes(row.NextPositionId), out var san))
                    named.Add((san, row.Count));
            }
            string text = FormatDistribution(named, witnesses);
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<Hash128, string> LegalSuccessors(Board board)
    {
        var result = new Dictionary<Hash128, string>();
        foreach (var move in MoveGen.Legal(board))
        {
            string san = San.ToSan(board, move);
            var next = board.Clone();
            MoveApply.Make(next, move);
            result[ChessCompose.PositionId(next)] = san;
        }
        return result;
    }

    private static async Task<string?> BookLineAsync(
        NpgsqlDataSource ds, string positionSurface, CancellationToken ct)
    {
        try
        {
            Hash128 posId;
            lock (ChessCompose.Gate) { posId = ChessCompose.PositionId(positionSurface); }

            var subject = await NpgsqlSubstrateReads.FirstAttestationSubjectAsync(
                ds, posId.ToBytes(), ExplainsRelation.ToBytes(), ct);
            if (subject is null) return null;

            var rendered = await NpgsqlSubstrateReads.RenderTextBatchAsync(
                ds, [subject], ct);
            if (rendered is { Length: > 0 } texts && IsSalientBook(texts[0]))
                return "Book: " + texts[0].Trim();
        }
        catch
        {
            // Commentary is decoration; a failed lookup must never affect play.
        }
        return null;
    }

    public static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.Trim();
        if (text.Length <= maxChars) return text;
        if (maxChars <= 1) return text[..maxChars];
        return text[..(maxChars - 1)] + "…";
    }

    private static string FormatEval(int scoreCp, int depth)
    {
        if (depth <= 0) return "";
        if (Math.Abs(scoreCp) >= 29_000)
            return scoreCp > 0 ? "Mating" : "Getting mated";
        double pawns = scoreCp / 100.0;
        string sign = pawns >= 0 ? "+" : "";
        return $"Eval {sign}{pawns:0.0} (d{depth})";
    }

    internal static string MotifLabel(string motif)
    {
        if (ChessConcepts.TryResolve(motif, chessDomain: true, out var concept))
            return concept.Name;
        return motif.Replace('_', ' ');
    }

    internal static string? FormatHistorical(
        string? playedOn, string mover, string? historicalSan, string? playedSan)
    {
        string? year = Year(playedOn);
        if (year is null || string.IsNullOrWhiteSpace(mover)) return null;

        if (!string.IsNullOrWhiteSpace(historicalSan))
        {
            if (!string.IsNullOrWhiteSpace(playedSan)
                && !string.Equals(historicalSan, playedSan, StringComparison.Ordinal))
                return $"{year}: {mover} had this exact position and played {historicalSan}, not {playedSan}.";
            return $"{year}: {mover} had this exact position and also played {historicalSan}.";
        }

        return $"{year}: {mover} also reached this exact position.";
    }

    internal static string? FormatUndated(string mover, string? historicalSan, string? playedSan)
    {
        if (string.IsNullOrWhiteSpace(mover)) return null;
        if (!string.IsNullOrWhiteSpace(historicalSan))
        {
            if (!string.IsNullOrWhiteSpace(playedSan)
                && !string.Equals(historicalSan, playedSan, StringComparison.Ordinal))
                return $"{mover} reached this position and played {historicalSan}, not {playedSan}.";
            return $"{mover} reached this position and played {historicalSan}.";
        }
        return $"{mover} reached this position.";
    }

    private static string? Year(string? playedOn)
    {
        if (string.IsNullOrWhiteSpace(playedOn) || playedOn.Length < 4) return null;
        return playedOn.Take(4).All(char.IsDigit) ? playedOn[..4] : null;
    }
}
