namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Generic UTF-8 span helpers shared by streaming line-oriented decomposers
/// (extracted from CILIDecomposer; nothing here is CILI-specific).
/// </summary>
public static class Utf8TextHelpers
{
    /// <summary>Trims leading/trailing ASCII whitespace (any byte &lt;= 0x20).</summary>
    public static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> span)
    {
        int start = 0, end = span.Length;
        while (start < end && span[start] <= (byte)' ') start++;
        while (end > start && span[end - 1] <= (byte)' ') end--;
        return span[start..end];
    }

    /// <summary>
    /// Extracts the contents of the first double-quoted Turtle string literal on the
    /// line, unescaping <c>\"</c> and <c>\\</c>. Returns null when no literal is present.
    /// </summary>
    public static byte[]? ExtractTurtleStringBytes(ReadOnlySpan<byte> span)
    {
        int first = span.IndexOf((byte)'"');
        if (first < 0) return null;
        int last = span.LastIndexOf((byte)'"');
        if (last <= first) return null;
        ReadOnlySpan<byte> inner = span[(first + 1)..last];
        if (inner.IndexOf((byte)'\\') < 0) return inner.ToArray();
        var buf = new byte[inner.Length];
        int n = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == (byte)'\\' && i + 1 < inner.Length)
            {
                i++;
                buf[n++] = inner[i] switch { (byte)'"' => (byte)'"', (byte)'\\' => (byte)'\\', _ => inner[i] };
            }
            else
            {
                buf[n++] = inner[i];
            }
        }
        return buf[..n];
    }
}
