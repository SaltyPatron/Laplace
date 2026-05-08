namespace Laplace.Pipeline;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Abstractions;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// IFireflyJar implementation. Stores per-(substrate entity × model)
/// firefly Point4D positions in the firefly_s3_extracted physicality
/// partition (separate from the codepoint_s3_substrate partition holding
/// substrate atom positions per CLAUDE.md invariant 7).
///
/// Per substrate invariant: fireflies are AI-MODEL-EXTRACTION ARTIFACTS,
/// NOT substrate primitives. Voronoi consensus over per-substrate-entity
/// firefly clouds emerges as multiple models contribute.
///
/// Phase 4 / Track F5 / Service F5/I.
///
/// The Get/query side is backed by a live PG query against the
/// physicality_firefly_s3_extracted partition (added in Phase 1.11). The
/// write side here is what the F5 ingestion pipeline drives.
/// </summary>
public sealed class FireflyJar : IFireflyJar
{
    private static readonly Point4D[] EmptyGeometry = System.Array.Empty<Point4D>();

    private readonly IConceptEntityResolver _resolver;
    private readonly IPhysicalityEmission   _emission;
    private readonly AtomId                 _fireflyPhysicalityType;

    public FireflyJar(IConceptEntityResolver resolver, IPhysicalityEmission emission)
    {
        _resolver               = resolver;
        _emission               = emission;
        _fireflyPhysicalityType = resolver.Resolve("firefly_s3_extracted");
    }

    public async Task StoreAsync(
        AtomId             substrateEntity,
        AtomId             modelEntity,
        Point4D            position,
        CancellationToken  cancellationToken)
    {
        var record = new PhysicalityRecord(
            PhysicalityTypeHash: _fireflyPhysicalityType,
            EntityHash:          substrateEntity,
            SourceHash:          modelEntity,
            Geometry:            new[] { position });
        await _emission.EmitAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read-side query against the firefly_s3_extracted physicality partition.
    /// Backed by a live PG query in production; not implemented in the
    /// in-memory ingestion path. Phase 1.11 adds the PG-backed query.
    /// </summary>
    public Task<IReadOnlyList<(AtomId Model, Point4D Position)>> GetForAsync(
        AtomId             substrateEntity,
        CancellationToken  cancellationToken)
    {
        // Concrete data store is the firefly_s3_extracted physicality partition;
        // read-side requires the live-PG query layer (#110 / Phase 1.11).
        // Until then: throw a clear NotImplemented so any caller wiring
        // the GET path discovers the missing infrastructure immediately
        // rather than getting an empty list silently.
        _ = substrateEntity;
        _ = cancellationToken;
        _ = EmptyGeometry;
        throw new System.NotImplementedException(
            "FireflyJar.GetForAsync requires the live-PG query layer (Phase 1.11 / task #110). " +
            "Until then, callers should query via direct SQL against the " +
            "physicality_firefly_s3_extracted partition.");
    }
}
