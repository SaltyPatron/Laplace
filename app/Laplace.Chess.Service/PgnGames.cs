using System.IO;
using System.Text;

namespace Laplace.Chess.Service;

/// <summary>
/// Shared PGN file helpers — game splitting + header-tag scanning — so the ingest decomposer
/// (<see cref="ChessPgnDecomposer"/>) and the offline analyzer (<see cref="ChessGameReview"/>) parse the
/// same way (converge, not fork). The tag scanners are the single home for <c>[Tag "value"]</c> reads;
/// <see cref="StreamGames"/> is the lazy splitter (O(one game) peak RAM, like the ingest path).
/// </summary>
internal static class PgnGames
{
    /// <summary>Stream a PGN file game-by-game: accumulate from one <c>[Event </c> tag to the next, yield
    /// each game's text. Lazy + O(one game) — safe on the 195 MB+ archives. UTF-8.</summary>
    public static IEnumerable<string> StreamGames(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sb = new StringBuilder(2048);
        bool inGame = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("[Event ", StringComparison.Ordinal))
            {
                if (inGame && sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                inGame = true;
            }
            if (inGame) { sb.Append(line); sb.Append('\n'); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Read an integer PGN tag value (<c>[Tag "1234"]</c>) by a cheap scan; 0 if absent/blank.</summary>
    public static int TagInt(string game, string tag)
    {
        int i = game.IndexOf("[" + tag + " \"", StringComparison.Ordinal);
        if (i < 0) return 0;
        i += tag.Length + 3;
        int j = game.IndexOf('"', i);
        return j > i && int.TryParse(game.AsSpan(i, j - i), out var v) ? v : 0;
    }

    /// <summary>Read a string PGN tag value (<c>[Tag "value"]</c>); "" if absent.</summary>
    public static string TagStr(string game, string tag)
    {
        int i = game.IndexOf("[" + tag + " \"", StringComparison.Ordinal);
        if (i < 0) return "";
        i += tag.Length + 3;
        int j = game.IndexOf('"', i);
        return j > i ? game[i..j].Trim() : "";
    }
}
