namespace Laplace.Core.Abstractions;

/// <summary>
/// P/Invoke surface for the native <c>UnicodeIcuService</c>. UAX29 boundary
/// detection (graphemes / words / sentences / line breaks) and all four
/// Unicode normalization forms (NFC / NFD / NFKC / NFKD) via ICU.
///
/// <see cref="ITextDecomposition"/> uses this to slice NFC-normalized text
/// into the tier-1 / tier-2 / tier-3 entities composed of codepoint atoms.
/// </summary>
public interface ITextSegmentation
{
    /// <summary>Boundary positions (in UTF-16 code-unit offsets) for grapheme clusters per UAX29 GB rules.</summary>
    int[] GraphemeBoundaries(string text);

    /// <summary>Boundary positions for word boundaries per UAX29 WB rules.</summary>
    int[] WordBoundaries(string text);

    /// <summary>Boundary positions for sentence boundaries per UAX29 SB rules.</summary>
    int[] SentenceBoundaries(string text);

    /// <summary>Boundary positions for line break opportunities per UAX14.</summary>
    int[] LineBoundaries(string text);

    /// <summary>NFC normalization (canonical composition).</summary>
    string NormalizeNfc(string text);

    /// <summary>NFD normalization (canonical decomposition).</summary>
    string NormalizeNfd(string text);

    /// <summary>NFKC normalization (compatibility composition).</summary>
    string NormalizeNfkc(string text);

    /// <summary>NFKD normalization (compatibility decomposition).</summary>
    string NormalizeNfkd(string text);
}
