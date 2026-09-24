using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class LanguageEntityId
{
    public static Hash128 FromIso639_3(string iso3Code)
    {
        ArgumentNullException.ThrowIfNull(iso3Code);
        string content = iso3Code.Trim();
        if (content.Length == 0)
            throw new ArgumentException("language code must not be empty", nameof(iso3Code));
        return ContentEmitter.RootId(content)
            ?? throw new InvalidOperationException($"language code could not be composed: {content}");
    }
}
