using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class LanguageEntityId
{
    public static Hash128 FromIso639_3(string iso3Code)
    {
        ArgumentNullException.ThrowIfNull(iso3Code);
        return Hash128.OfCanonical($"language:{iso3Code.ToLowerInvariant()}");
    }
}
