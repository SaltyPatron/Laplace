namespace Laplace.Decomposers.Abstractions;

public enum SpanKind { Prose, Code }

/// <summary>A contiguous run of a mixed document, classified prose or code (with a language).</summary>
public readonly record struct DocumentSpan(SpanKind Kind, string? Language, int Start, int Length);

/// <summary>
/// Splits a mixed document (an article/tutorial holding prose AND code) into prose vs. code spans,
/// so each routes to its law — prose to the text decomposer, code to the grammar for its language.
/// This deterministic pass handles Markdown fenced blocks (``` / ~~~ with an info string); inline
/// code, indented blocks, and bare-code-via-grammar-as-classifier are later refinements. The spans
/// share one document, so prose intent and code implementation co-occur (the Rosetta-stone effect).
/// </summary>
public static class DocumentRouter
{
    public static IReadOnlyList<DocumentSpan> Split(string doc)
    {
        var spans = new List<DocumentSpan>();
        if (string.IsNullOrEmpty(doc)) return spans;

        int n = doc.Length;
        int proseStart = 0;
        int i = 0;

        while (i < n)
        {
            int lineStart = i;
            int lineEnd = NextLine(doc, lineStart);
            ReadOnlySpan<char> line = doc.AsSpan(lineStart, lineEnd - lineStart);

            if (TryOpenFence(line, out char fenceChar, out int fenceLen, out string? lang))
            {
                if (lineStart > proseStart)
                    spans.Add(new DocumentSpan(SpanKind.Prose, null, proseStart, lineStart - proseStart));

                int codeStart = lineEnd;
                int codeEnd = n;
                int afterClose = n;
                int j = lineEnd;
                while (j < n)
                {
                    int ls = j;
                    int le = NextLine(doc, ls);
                    if (IsCloseFence(doc.AsSpan(ls, le - ls), fenceChar, fenceLen))
                    {
                        codeEnd = ls;        // code content ends at the close-fence line
                        afterClose = le;
                        break;
                    }
                    j = le;
                }

                if (codeEnd > codeStart)
                    spans.Add(new DocumentSpan(SpanKind.Code, lang, codeStart, codeEnd - codeStart));

                proseStart = afterClose;
                i = afterClose;
            }
            else
            {
                i = lineEnd;
            }
        }

        if (n > proseStart)
            spans.Add(new DocumentSpan(SpanKind.Prose, null, proseStart, n - proseStart));
        return spans;
    }

    // End index (exclusive) of the line starting at start, including its trailing '\n' if present.
    private static int NextLine(string doc, int start)
    {
        int nl = doc.IndexOf('\n', start);
        return nl < 0 ? doc.Length : nl + 1;
    }

    private static bool TryOpenFence(ReadOnlySpan<char> line, out char fenceChar, out int fenceLen, out string? lang)
    {
        fenceChar = '\0'; fenceLen = 0; lang = null;
        int p = 0;
        while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
        if (p >= line.Length || (line[p] != '`' && line[p] != '~')) return false;

        char c = line[p];
        int run = 0;
        while (p < line.Length && line[p] == c) { run++; p++; }
        if (run < 3) return false;

        // info string: first token after the fence is the language.
        while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
        int infoStart = p;
        while (p < line.Length && line[p] != ' ' && line[p] != '\t' && line[p] != '\r' && line[p] != '\n') p++;
        if (p > infoStart) lang = line.Slice(infoStart, p - infoStart).ToString().ToLowerInvariant();

        fenceChar = c; fenceLen = run;
        return true;
    }

    private static bool IsCloseFence(ReadOnlySpan<char> line, char fenceChar, int openLen)
    {
        int p = 0;
        while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
        int run = 0;
        while (p < line.Length && line[p] == fenceChar) { run++; p++; }
        if (run < openLen) return false;                 // close must be at least as long
        while (p < line.Length && (line[p] == ' ' || line[p] == '\t' || line[p] == '\r' || line[p] == '\n')) p++;
        return p >= line.Length;                          // nothing but whitespace after
    }
}
