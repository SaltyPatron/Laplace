namespace Laplace.Pipeline.Abstractions;

using Laplace.Core.Abstractions;

/// <summary>
/// Resolve substrate concept entities (edge types, roles, properties,
/// physicality types) by canonical name. Each name decomposes to its
/// codepoint LINESTRING; the resulting tier-1 composition entity hash IS
/// the concept's identity. Per substrate invariant 1: concepts are
/// content-addressed entities, NOT integer surrogate keys.
/// </summary>
public interface IConceptEntityResolver
{
    AtomId Resolve(string canonicalName);
}
