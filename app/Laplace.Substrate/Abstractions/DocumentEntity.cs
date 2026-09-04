using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// A document is a composition over admitted content, not an alias for a file and not an
/// alias for the text root.  The plain-text lane has one document per file today; format
/// providers can later supply several ordered content roots without changing file identity.
/// </summary>
public static class DocumentEntity
{
    public static Hash128 Resolve(Hash128 contentRoot)
    {
        Span<Hash128> constituents = stackalloc Hash128[1] { contentRoot };
        return Hash128.Merkle(EntityTier.Document, constituents);
    }

    public static Hash128 Emit(
        SubstrateChangeBuilder builder,
        Hash128 fileId,
        Hash128 contentRoot,
        byte[] canonicalContent)
    {
        Hash128 documentId = Resolve(contentRoot);
        builder.AddEntity(
            documentId,
            EntityTier.Document,
            EntityTypeRegistry.Document,
            fileId);

        if (TextEntityBuilder.TryDecomposeRoot(
                canonicalContent, out _, out _,
                out double x, out double y, out double z, out double m))
        {
            Span<double> coord = stackalloc double[4] { x, y, z, m };
            Hash128[] constituents = [contentRoot];
            Hash128 physicalityId = PhysicalityId.Compute(documentId, PhysicalityType.Content);
            if (builder.TrySeePhysicality(physicalityId))
            {
                builder.AddPhysicalityPreSeen(new PhysicalityRow(
                    Id: physicalityId,
                    EntityId: documentId,
                    SourceId: fileId,
                    Type: PhysicalityType.Content,
                    CoordX: x, CoordY: y, CoordZ: z, CoordM: m,
                    HilbertIndex: Hilbert128.Encode(coord),
                    TrajectoryXyzm: Trajectory.Build(constituents),
                    NConstituents: 1,
                    AlignmentResidual: null,
                    SourceDim: null,
                    ObservedAtUnixUs: 0));
            }
        }

        builder.AddAttestation(NativeAttestation.Categorical(
            fileId,
            "CONTAINS",
            documentId,
            fileId,
            SourceTrust.SubstrateMandate));
        return documentId;
    }
}
