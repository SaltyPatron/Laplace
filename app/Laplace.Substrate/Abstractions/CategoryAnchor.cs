using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Admits a readable category label as content. Opaque catalog keys go through
/// <see cref="ReferenceAnchor"/> instead.
/// </summary>
public static class CategoryAnchor
{
    // A category label is content; what it is follows from the claims that use it,
    // so admitting it records no type testimony.
    public static Hash128? Emit(SubstrateChangeBuilder b, string key, Hash128 source)
    {
        string? normalized = Normalize(key);
        return normalized is null ? null : ContentEmitter.Emit(b, normalized, source);
    }

    public static Hash128? Id(string key) =>
        Normalize(key) is { } normalized ? ContentEmitter.RootId(normalized) : null;

    private static string? Normalize(string key) =>
        string.IsNullOrEmpty(key) ? null : key.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
