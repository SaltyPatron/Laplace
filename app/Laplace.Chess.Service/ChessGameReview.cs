using System.Text;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public readonly record struct ReviewedMove(
    int MoveNo, bool White, string Played, string Best, int CpLoss, string Tag);

public sealed record ReviewedGame(
    string White, string Black, int WhiteElo, int BlackElo, GameOutcome? Result, int Plies,
    double WhiteAcpl, double BlackAcpl, int WhiteBlunders, int BlackBlunders, bool CrazyWin,
    int WinnerDownCp, IReadOnlyList<ReviewedMove> Worst);

public static class ChessGameReview
{
    public const int InaccuracyCp = 50, MistakeCp = 100, BlunderCp = 200;
    public const int ComebackCp = 300;
    private const int CplCap = 2000;
    private const int MateRange = 20_000;

    public static IReadOnlyList<ReviewedGame> ReviewFile(string path, int depth = 4, int maxGames = 0)
    {
        var outv = new List<ReviewedGame>();
        foreach (var file in Files(path))
        {
            foreach (var gameText in PgnGames.StreamGames(file))
            {
                if (ReviewGameText(gameText, depth) is { } g) outv.Add(g);
                if (maxGames > 0 && outv.Count >= maxGames) return outv;
            }
        }
        return outv;
    }

    public static ReviewedGame? ReviewGameText(string gameText, int depth = 4)
    {
        var bytes = Encoding.UTF8.GetBytes(gameText);
        List<string> sans; GameOutcome? result;
        using (var ast = GrammarDecomposer.Parse(bytes, "pgn"))
            (sans, result) = PgnMovetext.Extract(ast, bytes);
        if (sans.Count == 0) return null;
        if (result is null) return null;

        var m = new ChessModality();
        var best = new Search(EvalTerm.All);
        var verify = new Search(EvalTerm.All);
        var worst = new List<ReviewedMove>();
        double wSum = 0, bSum = 0; int wN = 0, bN = 0, wBlunders = 0, bBlunders = 0, plies = 0;
        int minWhiteEval = 0, maxWhiteEval = 0;

        var state = m.Initial();
        foreach (var san in sans)
        {
            var legal = m.LegalActions(state);
            var played = San.Resolve(state.Board, legal, san);
            if (played is null) break;
            bool white = state.Board.WhiteToMove;

            var br = best.Think(state.Board, new Search.Limits(MaxDepth: depth));
            int whiteEval = white ? br.Score : -br.Score;
            if (Math.Abs(whiteEval) < MateRange) { minWhiteEval = Math.Min(minWhiteEval, whiteEval); maxWhiteEval = Math.Max(maxWhiteEval, whiteEval); }
            int cpl = 0; string bestUci = br.BestMove?.ToUci() ?? "";
            if (br.BestMove is { } bm && bm.ToUci() != played.Value.ToUci())
            {
                var afterPlayed = m.Apply(state, played.Value);
                var pr = verify.Think(afterPlayed.Board, new Search.Limits(MaxDepth: Math.Max(1, depth - 1)));
                int playedValue = -pr.Score;
                cpl = Clamp(br.Score, playedValue);
            }

            string tag = cpl >= BlunderCp ? "blunder" : cpl >= MistakeCp ? "mistake"
                       : cpl >= InaccuracyCp ? "inaccuracy" : "";
            if (white) { wSum += cpl; wN++; if (cpl >= BlunderCp) wBlunders++; }
            else       { bSum += cpl; bN++; if (cpl >= BlunderCp) bBlunders++; }
            if (cpl >= InaccuracyCp)
                worst.Add(new ReviewedMove((plies / 2) + 1, white, played.Value.ToUci(), bestUci, cpl, tag));

            state = m.Apply(state, played.Value);
            plies++;
        }

        int winnerDownCp = 0;
        if (result is { IsDraw: false } r)
            winnerDownCp = r.Winner == 0 ? Math.Max(0, -minWhiteEval) : Math.Max(0, maxWhiteEval);
        bool crazyWin = winnerDownCp >= ComebackCp;

        worst.Sort((x, y) => y.CpLoss.CompareTo(x.CpLoss));
        return new ReviewedGame(
            PgnGames.TagStr(gameText, "White"), PgnGames.TagStr(gameText, "Black"),
            PgnGames.TagInt(gameText, "WhiteElo"), PgnGames.TagInt(gameText, "BlackElo"),
            result, plies,
            wN > 0 ? wSum / wN : 0, bN > 0 ? bSum / bN : 0,
            wBlunders, bBlunders, crazyWin, winnerDownCp,
            worst.Count > 5 ? worst.GetRange(0, 5) : worst);
    }

    private static int Clamp(int bestScore, int playedValue)
    {
        long diff = (long)bestScore - playedValue;
        if (diff < 0) diff = 0;
        if (Math.Abs(bestScore) > MateRange || Math.Abs(playedValue) > MateRange) diff = Math.Min(diff, CplCap);
        return (int)Math.Min(diff, CplCap);
    }

    private static IEnumerable<string> Files(string path)
    {
        if (File.Exists(path)) return new[] { path };
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*.pgn", SearchOption.AllDirectories)
                            .OrderBy(p => p, StringComparer.Ordinal);
        return Array.Empty<string>();
    }
}
