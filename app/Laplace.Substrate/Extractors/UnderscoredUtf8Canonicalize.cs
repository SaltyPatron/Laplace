using System.Text;

namespace Laplace.Decomposers.Extractors;

/// <summary>
/// Underscore normalization for sources that separate the words of a term with '_':
/// each '_' becomes an ASCII space (UTF-8 safe, since '_' is a single byte).
/// </summary>
public static class UnderscoredUtf8Canonicalize
{
    public static byte[] ToSpaces(ReadOnlySpan<byte> termUnderscored)
    {
        var bytes = termUnderscored.ToArray();
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'_') bytes[i] = (byte)' ';
        return bytes;
    }

    public static string ToSpaces(string termUnderscored) =>
        termUnderscored.Replace('_', ' ');

    public static byte[] ToSpacesBytes(string termUnderscored) =>
        ToSpaces(Encoding.UTF8.GetBytes(termUnderscored));
}
