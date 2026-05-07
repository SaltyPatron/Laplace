namespace Laplace.Pipeline.Abstractions;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Three-layer Glicko-2 (rated-source attestation):
///   - significance_source : trustworthiness of the source itself
///   - significance_entity : trustworthiness of THIS entity
///   - significance_edge   : strength of THIS edge's attestation
///
/// Trusted source observed/asserts X = weighted win for X scaled by source's
/// rating. NO competitive negative sampling. Absence of observation = high
/// RD (uncertainty), not low rating.
/// </summary>
public interface ISignificance
{
    /// <summary>Initial source rating from the source's curator-class prior (e.g. Unicode Consortium starts at 2000).</summary>
    Task InitializeSourceAsync(AtomId sourceHash, GlickoState initial, CancellationToken cancellationToken);

    /// <summary>Apply observations to a source's rating.</summary>
    Task UpdateSourceAsync(AtomId sourceHash, IReadOnlyList<(GlickoState Opponent, double Outcome)> observations, CancellationToken cancellationToken);

    Task UpdateEntityAsync(AtomId entityHash, IReadOnlyList<(GlickoState Opponent, double Outcome)> observations, CancellationToken cancellationToken);

    Task UpdateEdgeAsync(AtomId edgeTypeHash, AtomId edgeHash, IReadOnlyList<(GlickoState Opponent, double Outcome)> observations, CancellationToken cancellationToken);

    Task<GlickoState> GetSourceAsync(AtomId sourceHash, CancellationToken cancellationToken);
    Task<GlickoState> GetEntityAsync(AtomId entityHash, CancellationToken cancellationToken);
    Task<GlickoState> GetEdgeAsync(AtomId edgeTypeHash, AtomId edgeHash, CancellationToken cancellationToken);
}
