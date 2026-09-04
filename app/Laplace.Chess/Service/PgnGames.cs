using System.IO;
using System.Text;

namespace Laplace.Chess.Service;

internal static class PgnGames
{
    public static IEnumerable<string> StreamGames(string path)
    {
        // Provider exports are specified and served as UTF-8. Reject malformed input instead
        // of silently replacing bytes with U+FFFD and minting corrupted player/game identities.
        using var reader = new StreamReader(
            path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
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

    public static int TagInt(string game, string tag)
    {
        int i = game.IndexOf("[" + tag + " \"", StringComparison.Ordinal);
        if (i < 0) return 0;
        i += tag.Length + 3;
        int j = game.IndexOf('"', i);
        return j > i && int.TryParse(game.AsSpan(i, j - i), out var v) ? v : 0;
    }

    public static string TagStr(string game, string tag)
    {
        int i = game.IndexOf("[" + tag + " \"", StringComparison.Ordinal);
        if (i < 0) return "";
        i += tag.Length + 3;
        int j = game.IndexOf('"', i);
        return j > i ? game[i..j].Trim().Normalize(NormalizationForm.FormC) : "";
    }
}
