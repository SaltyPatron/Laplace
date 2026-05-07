namespace Laplace.Pipeline;

using System.Collections.Concurrent;
using System.Collections.Generic;

using Laplace.Core.Abstractions;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// Resolves substrate concept entities (edge types, roles, properties,
/// physicality types, etc.) by their canonical name — each name decomposes
/// to its codepoint LINESTRING and the resulting tier-1 composition entity
/// hash IS the concept's identity. NOT an English-named anchor lookup; the
/// concept entity is content-addressed like any other tier-1 composition.
///
/// Cached per-process so repeated lookups for "decomposition_of",
/// "is_a", etc. cost one Dictionary hit after first resolution.
///
/// Phase 2 / Track D / D4.
/// </summary>
public sealed class ConceptEntityResolver : IConceptEntityResolver
{
    private readonly ICodepointPool                       _codepoints;
    private readonly IIdentityHashing                     _hashing;
    private readonly ConcurrentDictionary<string, AtomId> _cache = new(System.StringComparer.Ordinal);

    public ConceptEntityResolver(ICodepointPool codepoints, IIdentityHashing hashing)
    {
        _codepoints = codepoints;
        _hashing    = hashing;
    }

    public AtomId Resolve(string canonicalName)
    {
        if (_cache.TryGetValue(canonicalName, out var cached))
        {
            return cached;
        }
        var children = new List<AtomId>(canonicalName.Length);
        var counts   = new List<int>(canonicalName.Length);
        foreach (var rune in canonicalName.EnumerateRunes())
        {
            children.Add(_codepoints.AtomIdFor(rune.Value));
            counts.Add(1);
        }
        var hash = _hashing.CompositionId(children, counts);
        _cache[canonicalName] = hash;
        return hash;
    }
}
