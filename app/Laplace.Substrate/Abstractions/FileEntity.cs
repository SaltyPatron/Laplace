using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// A file's own facts, content-addressed as a metadata DAG. Filesystem facts today;
/// extend with format-native metadata (EXIF / ID3 / PDF-XMP / a dump's own header) by
/// appending more canonical <c>key=value</c> lines — <c>same content = same hash</c> keeps
/// every derivation stable and lets identical metadata (a shared license, a shared dump
/// origin) mesh to one node across files.
/// </summary>
public readonly record struct FileMetadata(
    string Name,
    string RelativePath,
    long SizeBytes,
    DateTime ModifiedUtc)
{
    /// <summary>Deterministic, sorted, fixed-format serialization → the metadata-DAG root.</summary>
    public byte[] CanonicalUtf8() =>
        Encoding.UTF8.GetBytes(
            $"mtime={ModifiedUtc.ToUniversalTime():O}\n" +
            $"name={Name}\n" +
            $"path={RelativePath}\n" +
            $"size={SizeBytes}\n");

    public static FileMetadata FromPath(string absolutePath, string relativePath)
    {
        var fi = new FileInfo(absolutePath);
        return new FileMetadata(fi.Name, relativePath, fi.Length, fi.LastWriteTimeUtc);
    }
}

/// <summary>
/// Pillar 0 keystone: a file IS a content-entity — its <b>content DAG</b> composed with its
/// <b>metadata DAG</b> — and that composite hash IS the provenance <c>source_id</c>, computed
/// PER FILE, not a static per-decomposer label.
///
/// One change to what <c>source</c> means (it is already in attestation identity via
/// <c>NativeAttestation.ComputeId(subject,type,object,source,context)</c>) lands four fixes at once:
///   • mesh/corroboration — the same entry in two files is ONE content node with TWO distinct
///     file-witnesses (their metadata DAGs differ), so cross-source agreement is a real hash
///     collision, not one voice counted twice;
///   • dedup/completion — re-ingesting the same file collides on the file-entity hash → the
///     attestation identities collide → no-op, no <c>observation_count</c> mass-UPDATE, no marker;
///   • provenance/audit — <c>source_id</c> is now a walkable entity with its own metadata DAG,
///     reachable by <c>containers_of</c> from any content point (name / license / origin lineage).
///
/// LIVE in the document lane: <c>DocumentMultiFileStream</c> stamps each record with this
/// per-file source id, <c>DocumentIngestHandler.WalkWitness</c> deposits the completion
/// marker (<c>LayerCompletion.EmitFileMarker</c>) and the metadata DAG
/// (<see cref="EmitMetadata"/>), and <c>IngestExistenceGate</c> skips a marker-complete
/// file before compose. Other lanes still ride static sources — their conversion is the
/// tracked Pillar-0 follow-up campaign.
/// </summary>
public static class FileEntity
{
    /// <summary>Meta relation hanging a file's metadata DAG off its trunk. Same class as
    /// <c>HasLayerCompleted</c>: minted inline with its meta-type entity, never in
    /// relation_types.toml, never a highway bit, excluded from the consensus fold.</summary>
    public static readonly Hash128 MetadataRelationTypeId =
        Hash128.OfCanonical("substrate/type/HasFileMetadata/v1");

    /// <summary>
    /// Deposit the file's metadata DAG: the canonical metadata text becomes its own
    /// content DAG (identical metadata meshes to one node across files) attested onto the
    /// file trunk under the file's own provenance. Fetched at provenance-query time; never
    /// hashed into identity (that would fork the source per-name and destroy the collision
    /// that makes corroboration and dedup free).
    /// </summary>
    public static void EmitMetadata(
        SubstrateChangeBuilder builder, Hash128 fileRoot, in FileMetadata metadata)
    {
        if (ContentEmitter.Emit(builder, metadata.CanonicalUtf8(), fileRoot) is not { } metaRoot)
            return;
        builder
            .AddEntity(MetadataRelationTypeId, EntityTier.Word,
                BootstrapIntentBuilder.RelationTypeMetaTypeId, fileRoot)
            .AddAttestation(NativeAttestation.CategoricalResolved(
                fileRoot, MetadataRelationTypeId, metaRoot, fileRoot, contextId: null,
                SourceTrust.SubstrateMandate));
    }
    /// <summary>
    /// The file-entity <c>source_id</c> IS the file's content-DAG root — its trunk node. Nothing
    /// else: <c>same content = same file = same source</c>, by construction. Re-ingesting the same
    /// file resolves to the same root and no-ops; the same content living in another file is the
    /// SAME content node (that collision IS the mesh). The file's name/size/mtime/EXIF is a
    /// metadata DAG hung off this trunk and <b>fetched</b> when provenance is queried — it is NEVER
    /// hashed into the identity, because baking it in would fork the source per-name and destroy
    /// the collision that makes corroboration and dedup free.
    /// </summary>
    public static Hash128 SourceId(ReadOnlySpan<byte> contentUtf8) =>
        TextDecomposer.ContentRootId(contentUtf8)
            ?? throw new InvalidOperationException(
                "FileEntity.SourceId: content has no root (empty file)");
}
