namespace Laplace.Pipeline.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Resolve a substrate concept entity by its canonical name. Concept entities
/// are themselves substrate entities — compositions of their name's codepoint
/// LINESTRING. This interface is the antidote to hardcoded English enum
/// vocabularies in the schema.
///
/// Examples:
///   ResolveAsync("noun")           → the entity that IS the noun concept
///   ResolveAsync("script")         → the entity for the script property
///   ResolveAsync("Latin")          → the entity for the Latin script value
///   ResolveAsync("source")         → the entity for the source role
///   ResolveAsync("hypernym_of")    → the entity for the hypernym_of edge type
///   ResolveAsync("firefly_s3_extracted") → the entity for the firefly physicality type
///
/// Cached after first resolution. Created if not present (with Glicko-2
/// initialization at the system_computed source rating).
/// </summary>
public interface IConceptEntityResolver
{
    Task<AtomId> ResolveAsync(string canonicalName, CancellationToken cancellationToken);
}
