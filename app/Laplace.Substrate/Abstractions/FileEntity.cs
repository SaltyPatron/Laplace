using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Facts observed about one filesystem occurrence.
///
/// Identity-bearing fields are serialized separately from observations.  Name/path answer
/// which file occurrence this is and therefore participate in the file Merkle.  Size/mtime
/// describe an observation of that occurrence and belong in the ingest journal; touching a
/// file must not mint a new semantic file merely because its mtime changed.
/// </summary>
public readonly record struct FileMetadata(
    string Name,
    string RelativePath,
    long SizeBytes,
    DateTime ModifiedUtc)
{
    /// <summary>Stable file-occurrence identity metadata.</summary>
    public byte[] IdentityCanonicalUtf8() =>
        Encoding.UTF8.GetBytes(
            $"name={Name}\n" +
            $"path={RelativePath.Replace('\\', '/')}\n");

    /// <summary>Observed filesystem facts; never part of <see cref="FileEntity.Resolve"/>.</summary>
    public byte[] ObservationCanonicalUtf8() =>
        Encoding.UTF8.GetBytes(
            $"mtime={ModifiedUtc.ToUniversalTime():O}\n" +
            $"size={SizeBytes}\n");

    /// <summary>
    /// Compatibility spelling for callers that only need the identity metadata DAG.
    /// New code should say <see cref="IdentityCanonicalUtf8"/> explicitly.
    /// </summary>
    public byte[] CanonicalUtf8() => IdentityCanonicalUtf8();

    public static FileMetadata FromPath(string absolutePath, string relativePath)
    {
        var fi = new FileInfo(absolutePath);
        return new FileMetadata(fi.Name, relativePath, fi.Length, fi.LastWriteTimeUtc);
    }
}

/// <summary>The three identities that make one file occurrence.</summary>
public readonly record struct FileIdentity(
    Hash128 ContentRootId,
    Hash128 MetadataRootId,
    Hash128 FileId);

/// <summary>
/// A file is a real composition in the Merkle DAG:
///
/// <c>file_id = Merkle(Document, [content_root, identity_metadata_root])</c>.
///
/// The content node remains globally shared.  The metadata node is also ordinary content.
/// The file id composes the two, so identical text at two paths is one content tree but two
/// file occurrences.  Provenance then walks content -> document -> file -> corpus/user.
/// </summary>
public static class FileEntity
{
    /// <summary>
    /// Legacy metadata-edge id retained so old seeded rows remain readable. New file writes
    /// use the file physicality's ordered [content, metadata] constituents instead of an
    /// out-of-band HasFileMetadata attestation.
    /// </summary>
    public static readonly Hash128 MetadataRelationTypeId =
        SubstrateCanonicalIds.OfVersioned("type", "HasFileMetadata");

    public static FileIdentity Resolve(ReadOnlySpan<byte> contentUtf8, in FileMetadata metadata)
    {
        Hash128 contentRoot = ContentTierSpine.ResolveRoot(contentUtf8)
            ?? throw new InvalidOperationException(
                "FileEntity.Resolve: content has no root (empty or invalid content)");

        byte[] metadataUtf8 = metadata.IdentityCanonicalUtf8();
        Hash128 metadataRoot = ContentTierSpine.ResolveRoot(metadataUtf8)
            ?? throw new InvalidOperationException(
                "FileEntity.Resolve: identity metadata has no content root");

        Span<Hash128> constituents = stackalloc Hash128[2]
        {
            contentRoot,
            metadataRoot,
        };
        Hash128 fileId = Hash128.Merkle(EntityTier.Document, constituents);
        return new FileIdentity(contentRoot, metadataRoot, fileId);
    }

    /// <summary>
    /// Stage the identity-metadata content tree and the file composition itself. The raw
    /// content tree is staged by the modality/document handler; it is intentionally not
    /// duplicated here. The returned file id can be used directly as the click/export id.
    /// </summary>
    public static FileIdentity Emit(
        SubstrateChangeBuilder builder,
        Hash128 parentSourceId,
        byte[] canonicalContent,
        in FileMetadata metadata)
    {
        var identity = Resolve(canonicalContent, metadata);
        byte[] metadataUtf8 = metadata.IdentityCanonicalUtf8();

        Hash128? emittedMetadata = ContentEmitter.Emit(builder, metadataUtf8, identity.FileId);
        if (emittedMetadata is not { } metaRoot || metaRoot != identity.MetadataRootId)
            throw new InvalidOperationException(
                "FileEntity.Emit: metadata emission disagreed with resolved metadata identity");

        builder.AddEntity(
            identity.FileId,
            EntityTier.Document,
            EntityTypeRegistry.SourceFile,
            parentSourceId);

        if (TryRootCoordinate(canonicalContent, out var contentCoord)
            && TryRootCoordinate(metadataUtf8, out var metadataCoord))
        {
            double[] coordinates =
            [
                contentCoord[0], contentCoord[1], contentCoord[2], contentCoord[3],
                metadataCoord[0], metadataCoord[1], metadataCoord[2], metadataCoord[3],
            ];
            double[] center = Math4d.KarcherMean(coordinates);
            Span<double> centerSpan = center;
            Hash128[] constituents = [identity.ContentRootId, identity.MetadataRootId];
            Hash128 physicalityId = PhysicalityId.Compute(identity.FileId, PhysicalityType.Content);
            if (builder.TrySeePhysicality(physicalityId))
            {
                builder.AddPhysicalityPreSeen(new PhysicalityRow(
                    Id: physicalityId,
                    EntityId: identity.FileId,
                    SourceId: parentSourceId,
                    Type: PhysicalityType.Content,
                    CoordX: center[0], CoordY: center[1], CoordZ: center[2], CoordM: center[3],
                    HilbertIndex: Hilbert128.Encode(centerSpan),
                    TrajectoryXyzm: Trajectory.Build(constituents),
                    NConstituents: constituents.Length,
                    AlignmentResidual: null,
                    SourceDim: null,
                    ObservedAtUnixUs: 0));
            }
        }

        return identity;
    }

    /// <summary>
    /// Compatibility helper for old call sites. It deposits the legacy metadata edge but
    /// does not define file identity. New document/file paths call <see cref="Emit"/>.
    /// </summary>
    public static void EmitMetadata(
        SubstrateChangeBuilder builder, Hash128 fileRoot, in FileMetadata metadata)
    {
        if (ContentEmitter.Emit(builder, metadata.IdentityCanonicalUtf8(), fileRoot) is not { } metaRoot)
            return;
        builder
            .AddEntity(MetadataRelationTypeId, EntityTier.Word,
                BootstrapIntentBuilder.RelationTypeMetaTypeId, fileRoot)
            .AddAttestation(NativeAttestation.CategoricalResolved(
                fileRoot, MetadataRelationTypeId, metaRoot, fileRoot, contextId: null,
                SourceTrust.SubstrateMandate));
    }

    /// <summary>
    /// Compatibility API: callers that only have bytes cannot identify a file occurrence,
    /// because path/name are part of file identity. This returns the content root only and
    /// must not be used as a file id.
    /// </summary>
    public static Hash128 SourceId(ReadOnlySpan<byte> contentUtf8) =>
        TextDecomposer.ContentRootId(contentUtf8)
            ?? throw new InvalidOperationException(
                "FileEntity.SourceId: content has no root (empty file)");

    private static bool TryRootCoordinate(byte[] canonical, out double[] coord)
    {
        coord = new double[4];
        if (!TextEntityBuilder.TryDecomposeRoot(
                canonical, out _, out _, out double x, out double y, out double z, out double m))
            return false;
        coord[0] = x;
        coord[1] = y;
        coord[2] = z;
        coord[3] = m;
        return true;
    }
}
