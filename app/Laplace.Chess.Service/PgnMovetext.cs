using System.Text;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// Shared extraction of a game's MAINLINE SAN tokens (and the optional terminating result) from the
/// <c>pgn</c> tree-sitter AST. Both <see cref="ChessPgnDecomposer"/> (chess.com / lichess game files)
/// and <see cref="ChessOpeningsDecomposer"/> (the ECO opening-book TSV) parse their movetext through the
/// SAME grammar — converge, not fork. Variations, comments, NAGs and clocks are dropped by the grammar
/// walk; only ordered mainline moves remain.
/// </summary>
internal static class PgnMovetext
{
    /// <summary>
    /// Single pass over the AST: ordered mainline SAN tokens plus the terminating result, or
    /// <c>null</c> result when there is none (e.g. a bare opening line with no <c>1-0</c>/<c>0-1</c>/
    /// <c>1/2-1/2</c>).
    /// </summary>
    public static (List<string> Moves, GameOutcome? Result) Extract(GrammarAst ast, byte[] utf8)
    {
        var moves = new List<string>();
        GameOutcome? result = null;
        int n = ast.NodeCount;
        for (int i = 0; i < n; i++)
        {
            var node = ast.GetNode(i);
            var name = ast.NodeTypeName(node.NodeTypeId);
            if (name == "san_move")
            {
                if (!InsideVariation(ast, node)) moves.Add(Text(utf8, node));
            }
            else if (name == "game_result")
            {
                result = ParseResult(Text(utf8, node));
            }
        }
        return (moves, result);
    }

    private static bool InsideVariation(GrammarAst ast, LaplaceAstNode node)
    {
        uint p = node.Parent;
        while (p != GrammarAst.Root)
        {
            var pn = ast.GetNode((int)p);
            if (ast.NodeTypeName(pn.NodeTypeId) == "variation") return true;
            p = pn.Parent;
        }
        return false;
    }

    private static string Text(byte[] utf8, LaplaceAstNode node)
        => Encoding.UTF8.GetString(utf8, (int)node.StartByte, (int)(node.EndByte - node.StartByte)).Trim();

    private static GameOutcome? ParseResult(string r) => r switch
    {
        "1-0" => GameOutcome.WonBy(0),
        "0-1" => GameOutcome.WonBy(1),
        "1/2-1/2" => GameOutcome.Draw,
        _ => null, // "*" — unfinished, no outcome to learn from
    };
}
