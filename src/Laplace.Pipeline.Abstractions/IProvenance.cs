namespace Laplace.Pipeline.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Resolve substrate provenance source entities (sources are entities) and
/// emit per-entity / per-edge provenance edges. Trust priors per source
/// (Unicode Consortium / SIL / Princeton / OMW / UD / Wiktionary / Tatoeba /
/// AI models / user / random web) come from <see cref="ISignificance"/> and
/// flow into Glicko-2 source rating updates.
/// </summary>
public interface IProvenance
{
    /// <summary>
    /// Resolve a substrate source entity by canonical name. Sources like
    /// <c>iso_639_3_registry</c> or <c>princeton_wordnet_3_0</c> are
    /// themselves substrate entities (compositions of their name's codepoint
    /// LINESTRING).
    /// </summary>
    Task<AtomId> ResolveSourceAsync(string canonicalName, CancellationToken cancellationToken);

    ValueTask EmitEntityProvenanceAsync(EntityProvenanceRecord record, CancellationToken cancellationToken);

    ValueTask EmitEdgeProvenanceAsync(EdgeProvenanceRecord record, CancellationToken cancellationToken);
}
