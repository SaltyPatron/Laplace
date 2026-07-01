using System.Text;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

internal static class PgnMovetext
{
    public sealed record PgnMoveStream(
        string San,
        int PlyIndex,
        bool InVariation,
        string? CommentText = null,
        int? Nag = null,
        string? StandaloneAnnotation = null,
        string? SuffixAnnotation = null);

    public sealed record PgnWalkResult(
        IReadOnlyList<PgnMoveStream> Mainline,
        IReadOnlyList<PgnMoveStream> AllPlies,
        GameOutcome? Result);

    public static (List<string> Moves, GameOutcome? Result) Extract(GrammarAst ast, byte[] utf8)
    {
        var walk = Walk(ast, utf8);
        return (walk.Mainline.Select(m => m.San).ToList(), walk.Result);
    }

    public static PgnWalkResult Walk(GrammarAst ast, byte[] utf8)
    {
        var all = new List<PgnMoveStream>();
        GameOutcome? result = null;
        string? pendingComment = null;
        int? pendingNag = null;
        string? pendingStandalone = null;
        int mainlinePly = 0;

        int n = ast.NodeCount;
        for (int i = 0; i < n; i++)
        {
            var node = ast.GetNode(i);
            var name = ast.NodeTypeName(node.NodeTypeId);
            switch (name)
            {
                case "comment":
                    ApplyComment(all, CommentBody(utf8, node), ref pendingComment);
                    break;
                case "nag":
                    ApplyNag(all, ParseNag(Text(utf8, node)), ref pendingNag);
                    break;
                case "annotation":
                    ApplyStandalone(all, Text(utf8, node), ref pendingStandalone);
                    break;
                case "san_move":
                {
                    bool inVar = InsideVariation(ast, node);
                    string raw = Text(utf8, node);
                    all.Add(new PgnMoveStream(
                        raw,
                        inVar ? -1 : mainlinePly,
                        inVar,
                        pendingComment,
                        pendingNag,
                        pendingStandalone,
                        ExtractSuffixAnnotation(raw)));
                    if (!inVar) mainlinePly++;
                    pendingComment = null;
                    pendingNag = null;
                    pendingStandalone = null;
                    break;
                }
                case "game_result":
                    result = ParseResult(Text(utf8, node));
                    break;
            }
        }
        var mainline = all.Where(p => !p.InVariation).ToList();
        return new PgnWalkResult(mainline, all, result);
    }

    private static void ApplyComment(List<PgnMoveStream> all, string text, ref string? pending)
    {
        if (all.Count > 0 && pending is null)
            all[^1] = all[^1] with { CommentText = text };
        else pending = text;
    }

    private static void ApplyNag(List<PgnMoveStream> all, int? nag, ref int? pending)
    {
        if (nag is null) return;
        if (all.Count > 0 && pending is null)
            all[^1] = all[^1] with { Nag = nag };
        else pending = nag;
    }

    private static void ApplyStandalone(List<PgnMoveStream> all, string ann, ref string? pending)
    {
        if (all.Count > 0 && pending is null)
            all[^1] = all[^1] with { StandaloneAnnotation = ann };
        else pending = ann;
    }

    private static string CommentBody(byte[] utf8, LaplaceAstNode node)
    {
        string raw = Text(utf8, node);
        if (raw.Length >= 2 && raw[0] == '{' && raw[^1] == '}')
            return raw[1..^1].Trim();
        return raw;
    }

    private static int? ParseNag(string text)
    {
        if (text.Length < 2 || text[0] != '$') return null;
        return int.TryParse(text[1..], out int v) ? v : null;
    }

    internal static string? ExtractSuffixAnnotation(string raw)
    {
        int end = raw.Length;
        while (end > 0 && raw[end - 1] is '+' or '#') end--;
        int start = end;
        while (start > 0 && raw[start - 1] is '!' or '?') start--;
        return start < end ? raw[start..end] : null;
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
        _ => null,
    };
}
