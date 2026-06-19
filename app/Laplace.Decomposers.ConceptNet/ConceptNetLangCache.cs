using System.Collections.Concurrent;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.ConceptNet;

internal static class ConceptNetLangCache
{
    private static readonly ConcurrentDictionary<string, Hash128> Ids = new(StringComparer.Ordinal);

    public static Hash128 Resolve(ReadOnlySpan<byte> langUtf8)
    {
        if (langUtf8.IsEmpty) return LanguageReference.Resolve("und");
        if (langUtf8.Length <= 8)
        {
            Span<char> chars = stackalloc char[8];
            int n = Encoding.UTF8.GetChars(langUtf8, chars);
            string key = new string(chars[..n]);
            return Ids.GetOrAdd(key, static k => LanguageReference.Resolve(k));
        }
        return LanguageReference.Resolve(Encoding.UTF8.GetString(langUtf8));
    }
}
