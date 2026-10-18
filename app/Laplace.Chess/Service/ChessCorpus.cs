using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// The corpus a game was taken from. The name is the witness source. A path or a
/// Site tag selects it; a game with neither stays on the ordinary PGN source.
/// </summary>
public static class ChessCorpus
{
    public const string Otb = "lumbrasgigabase-otb";
    public const string Online = "lumbrasgigabase-online";
    public const string Twic = "twic";
    public const string Lichess = "lichess";
    public const string ChessCom = "chesscom";

    public static readonly string[] Names = [Otb, Online, Twic, Lichess, ChessCom];

    public static Hash128 SourceId(string corpus) => SubstrateCanonicalIds.Source(corpus);

    public static string? FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string norm = path.Replace('\\', '/').ToLowerInvariant();
        if (norm.Contains("lumbrasgigabase-online")
            || (norm.Contains("lumbras") && norm.Contains("online")))
            return Online;
        if (norm.Contains("lumbrasgigabase-otb")
            || (norm.Contains("lumbras") && ContainsSegment(norm, "otb")))
            return Otb;
        if (ContainsSegment(norm, Twic)) return Twic;
        if (ContainsSegment(norm, Lichess)) return Lichess;
        if (ContainsSegment(norm, ChessCom) || norm.Contains("chess.com")) return ChessCom;
        return null;
    }

    public static string? FromSite(string? site)
    {
        if (string.IsNullOrWhiteSpace(site)) return null;
        string norm = site.ToLowerInvariant();
        if (norm.Contains("lichess.org")) return Lichess;
        if (norm.Contains("chess.com")) return ChessCom;
        return null;
    }

    private static bool ContainsSegment(string norm, string segment)
    {
        foreach (string part in norm.Split('/'))
        {
            if (part.Equals(segment, StringComparison.Ordinal)
                || part.StartsWith(segment + ".", StringComparison.Ordinal)
                || part.StartsWith(segment + "_", StringComparison.Ordinal)
                || part.StartsWith(segment + "-", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
