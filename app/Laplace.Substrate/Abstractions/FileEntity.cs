using System.Text;
using Laplace.Engine.Core;

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
/// See <c>~/.claude/plans/quirky-moseying-cray.md</c> Pillar 0.
/// </summary>
public static class FileEntity
{
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
