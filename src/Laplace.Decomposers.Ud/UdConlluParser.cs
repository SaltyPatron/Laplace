namespace Laplace.Decomposers.Ud;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Streaming CoNLL-U parser for Universal Dependencies treebanks. Yields
/// one <see cref="UdSentence"/> per blank-line-delimited block. Comment
/// lines beginning with <c>#</c> may carry <c># sent_id = X</c> and
/// <c># text = Y</c> metadata.
/// </summary>
public sealed class UdConlluParser
{
    public static IEnumerable<UdSentence> Parse(string path)
    {
        using var reader = new StreamReader(path);
        var current = new List<UdToken>();
        string? sentenceId = null;
        string? text       = null;
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0)
            {
                if (current.Count > 0)
                {
                    yield return new UdSentence(sentenceId ?? string.Empty, text ?? string.Empty, current);
                    current = new List<UdToken>();
                    sentenceId = null;
                    text = null;
                }
                continue;
            }
            if (line[0] == '#')
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq >= 0)
                {
                    var key = line[1..eq].Trim();
                    var val = line[(eq + 1)..].Trim();
                    if (key.Equals("sent_id", StringComparison.Ordinal)) { sentenceId = val; }
                    else if (key.Equals("text", StringComparison.Ordinal)) { text = val; }
                }
                continue;
            }
            var parts = line.Split('\t');
            if (parts.Length < 10) { continue; }
            current.Add(new UdToken(
                Id:     parts[0],
                Form:   parts[1],
                Lemma:  parts[2],
                Upos:   parts[3],
                Xpos:   parts[4],
                Feats:  ParseFeats(parts[5]),
                Head:   parts[6],
                Deprel: parts[7],
                Deps:   parts[8],
                Misc:   parts[9]));
        }
        if (current.Count > 0)
        {
            yield return new UdSentence(sentenceId ?? string.Empty, text ?? string.Empty, current);
        }
    }

    private static Dictionary<string, string> ParseFeats(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "_")
        {
            return new Dictionary<string, string>(0);
        }
        var pairs = s.Split('|');
        var result = new Dictionary<string, string>(pairs.Length, StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) { continue; }
            result[pair[..eq]] = pair[(eq + 1)..];
        }
        return result;
    }
}
