using System.Text;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>One reviewed ply: what was played, the engine's best, and the centipawn-loss between them.</summary>
public readonly record struct ReviewedMove(
    int MoveNo, bool White, string Played, string Best, int CpLoss, string Tag);

/// <summary>One reviewed game: players/result + per-side average centipawn-loss (ACPL), blunder counts,
/// the "crazy win" flag (the winner was objectively LOSING at some point yet still won — a comeback, not
/// merely a single blunder), how far down the winner was at worst (<paramref name="WinnerDownCp"/>, ≥0),
/// and the worst moves.</summary>
public sealed record ReviewedGame(
    string White, string Black, int WhiteElo, int BlackElo, GameOutcome? Result, int Plies,
    double WhiteAcpl, double BlackAcpl, int WhiteBlunders, int BlackBlunders, bool CrazyWin,
    int WinnerDownCp, IReadOnlyList<ReviewedMove> Worst);

/// <summary>
/// Offline game-review over INGESTED games (the corpus-mining lane): replay each PGN game and, at every
/// ply, run the alpha-beta <see cref="Search"/> to get the engine's best move + score, then score the
/// move actually played — the gap is its centipawn-loss (inaccuracy / mistake / blunder). Surfaces
/// per-side ACPL and the <b>"crazy win"</b> (a side blundered but still won) — the user's idea: those are
/// where deep re-search later discriminates dumb luck from an eval blind-spot (the latter being exactly
/// where a substrate-learned overlay should override PeSTO). Reuses the shared PGN path
/// (<see cref="PgnGames"/> + <see cref="PgnMovetext"/>); pure search, no DB.
/// </summary>
public static class ChessGameReview
{
    public const int InaccuracyCp = 50, MistakeCp = 100, BlunderCp = 200;
    public const int ComebackCp = 300;     // a "crazy win": the winner was ≥3 pawns down yet won
    private const int CplCap = 2000;       // clamp mate-swing losses so labels stay sane
    private const int MateRange = 20_000;  // scores beyond this are forced-mate, not eval

    /// <summary>Review up to <paramref name="maxGames"/> games (0 = all) from a PGN file/dir.</summary>
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

    /// <summary>Review a single game's PGN text; null if it has no result or no legal mainline.</summary>
    public static ReviewedGame? ReviewGameText(string gameText, int depth = 4)
    {
        var bytes = Encoding.UTF8.GetBytes(gameText);
        List<string> sans; GameOutcome? result;
        using (var ast = GrammarDecomposer.Parse(bytes, "pgn"))
            (sans, result) = PgnMovetext.Extract(ast, bytes);
        if (sans.Count == 0) return null;
        if (result is null) return null;     // "*" / abandoned game — no signal for outcome analysis

        var m = new ChessModality();
        var best = new Search(EvalTerm.All);
        var verify = new Search(EvalTerm.All);
        var worst = new List<ReviewedMove>();
        double wSum = 0, bSum = 0; int wN = 0, bN = 0, wBlunders = 0, bBlunders = 0, plies = 0;
        // Track the position eval from WHITE's perspective across the game, to detect a comeback.
        int minWhiteEval = 0, maxWhiteEval = 0;

        var state = m.Initial();
        foreach (var san in sans)
        {
            var legal = m.LegalActions(state);
            var played = San.Resolve(state.Board, legal, san);
            if (played is null) break;                  // malformed/illegal token → stop at the clean prefix
            bool white = state.Board.WhiteToMove;

            var br = best.Think(state.Board, new Search.Limits(MaxDepth: depth));
            // br.Score is side-to-move relative; convert to White's perspective for the comeback track.
            int whiteEval = white ? br.Score : -br.Score;
            if (Math.Abs(whiteEval) < MateRange) { minWhiteEval = Math.Min(minWhiteEval, whiteEval); maxWhiteEval = Math.Max(maxWhiteEval, whiteEval); }
            int cpl = 0; string bestUci = br.BestMove?.ToUci() ?? "";
            if (br.BestMove is { } bm && bm.ToUci() != played.Value.ToUci())
            {
                // Value of the move actually played = negamax reflection of the resulting position.
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

        // A "crazy win" = a COMEBACK: the eventual winner was objectively losing (≥ ComebackCp) at some
        // point yet still won. That's the discriminating signal (at low Elo a single blunder is universal);
        // these are where deep re-search separates luck (the opponent failed to convert) from an eval
        // blind-spot (the "losing" side was actually fine and the eval was wrong).
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

    // Centipawn-loss between the best score and the played-move value (both side-to-move relative,
    // same side), clamped so a thrown-away forced mate doesn't produce an absurd number.
    private static int Clamp(int bestScore, int playedValue)
    {
        long diff = (long)bestScore - playedValue;
        if (diff < 0) diff = 0;                         // search noise / equal lines
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
