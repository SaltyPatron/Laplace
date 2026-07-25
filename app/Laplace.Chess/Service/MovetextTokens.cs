using System.Text;

namespace Laplace.Chess.Service;

/// <summary>
/// The movetext's OWN natural units.
///
/// A PGN movetext was being handed whole to the shared text spine, which applies UAX #29
/// sentence segmentation — and PGN's move-number separator is '.', so it split mid-line into
/// fragments like "Nd2 Nf6 4. e5 Nfd7 5. ". Measured over 3,000 games: 81,373 constituents,
/// 67,108 of them distinct. 82.5% single-use. Content addressing earns its keep by COLLIDING,
/// and prose segmentation of a non-prose format produces almost none — while filling tier-3
/// content space with fragments that can never corroborate with anything, chess or otherwise.
///
/// The real units are plies and the tokens around them. There are only a few thousand distinct
/// SAN tokens in all of chess, so "Nf6" should be ONE entity witnessed millions of times.
///
/// Tokenization is verbatim and structure-aware, not a naive split:
///   * brace comments stay whole — {[%clk 0:02:59.8]} is one token, not three
///   * parenthesised variations stay whole, so a RAV never fragments
///   * everything else splits on whitespace: move numbers, SAN, NAGs, the result token
/// Joining the tokens with single spaces reproduces the movetext. PGN whitespace carries no
/// information — a line break mid-variation and a space mean the same game — so the token
/// sequence IS the lossless record, and it is the SOURCE'S units rather than English's.
/// </summary>
public static class MovetextTokens
{
    /// <summary>Verbatim tokens, in order. Braces and parens are kept intact.</summary>
    public static List<string> Parse(string movetext)
    {
        var outv = new List<string>(256);
        if (string.IsNullOrEmpty(movetext)) return outv;

        var sb = new StringBuilder(32);
        int depthBrace = 0, depthParen = 0;

        void Flush()
        {
            if (sb.Length > 0) { outv.Add(sb.ToString()); sb.Clear(); }
        }

        foreach (char c in movetext)
        {
            if (c == '{') depthBrace++;
            else if (c == '}' && depthBrace > 0) depthBrace--;
            else if (c == '(') depthParen++;
            else if (c == ')' && depthParen > 0) depthParen--;

            if (char.IsWhiteSpace(c) && depthBrace == 0 && depthParen == 0)
            {
                Flush();
                continue;
            }
            sb.Append(c);
        }
        Flush();
        return outv;
    }

    /// <summary>
    /// The canonical surface a token sequence rebuilds to. Single-space joined: the form the
    /// tokens compose back into, and what a reader of the record sees.
    /// </summary>
    public static string Canonical(IReadOnlyList<string> tokens) => string.Join(' ', tokens);
}
