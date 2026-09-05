using System.Text;
using System.Text.Json;
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
    DateTime ModifiedUtc,
    string? Modality = null,
    DocumentFormatMetadata? FormatMetadata = null)
{
    /// <summary>Stable file-occurrence identity metadata.</summary>
    public byte[] IdentityCanonicalUtf8()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("name", Name);
        writer.WriteString("path", RelativePath.Replace('\\', '/'));
        if (Modality is { Length: > 0 }) writer.WriteString("modality", Modality);
        if (FormatMetadata is { } native)
        {
            writer.WritePropertyName("formatMetadata");
            writer.WriteStartObject();
            writer.WriteString("format", native.Format);
            Write(writer, "ebookId", native.EbookId);
            Write(writer, "title", native.Title);
            Write(writer, "author", native.Author);
            Write(writer, "language", native.Language);
            Write(writer, "releaseDate", native.ReleaseDate);
            Write(writer, "updatedDate", native.UpdatedDate);
            Write(writer, "credits", native.Credits);
            if (native.HeaderBoundaryByteOffset is { } offset)
                writer.WriteNumber("headerBoundaryByteOffset", offset);
            Write(writer, "headerBoundary", native.HeaderBoundary);
            if (!native.HeaderStatus.Equals("complete", StringComparison.Ordinal))
                writer.WriteString("headerStatus", native.HeaderStatus);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    public static FileMetadata ParseIdentityCanonicalUtf8(ReadOnlyMemory<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() is < 2 or > 4
                || !root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("path", out var path)
                || path.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("invalid file identity metadata");
            foreach (JsonProperty property in root.EnumerateObject())
                if (property.Name is not "name" and not "path" and not "modality"
                    and not "formatMetadata")
                    throw new InvalidDataException("invalid file identity metadata");
            string? modality = null;
            if (root.TryGetProperty("modality", out var modalityValue))
            {
                if (modalityValue.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(modalityValue.GetString()))
                    throw new InvalidDataException("invalid file identity metadata");
                modality = modalityValue.GetString();
            }
            DocumentFormatMetadata? formatMetadata = root.TryGetProperty(
                "formatMetadata", out var nativeValue)
                ? ParseFormatMetadata(nativeValue)
                : null;
            return new FileMetadata(
                name.GetString()!, path.GetString()!, 0, DateTime.UnixEpoch,
                modality, formatMetadata);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("invalid file identity metadata", exception);
        }
    }

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

    private static void Write(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null) writer.WriteString(name, value);
    }

    private static DocumentFormatMetadata ParseFormatMetadata(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("format", out JsonElement format)
            || format.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(format.GetString()))
            throw new InvalidDataException("invalid format-native file metadata");
        string[] allowed =
        [
            "format", "ebookId", "title", "author", "language", "releaseDate",
            "updatedDate", "credits", "headerBoundaryByteOffset", "headerBoundary",
            "headerStatus",
        ];
        foreach (JsonProperty property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("invalid format-native file metadata");
        long? boundaryOffset = null;
        if (value.TryGetProperty("headerBoundaryByteOffset", out JsonElement offset))
        {
            if (!offset.TryGetInt64(out long parsedOffset) || parsedOffset < 0)
                throw new InvalidDataException("invalid format-native file metadata");
            boundaryOffset = parsedOffset;
        }
        string? boundary = String(value, "headerBoundary");
        if ((boundaryOffset is null) != (boundary is null))
            throw new InvalidDataException("incomplete format-native header boundary");
        string headerStatus = String(value, "headerStatus") ?? "complete";
        if (string.IsNullOrWhiteSpace(headerStatus))
            throw new InvalidDataException("invalid format-native header status");
        return new DocumentFormatMetadata(
            format.GetString()!,
            String(value, "ebookId"),
            String(value, "title"),
            String(value, "author"),
            String(value, "language"),
            String(value, "releaseDate"),
            String(value, "updatedDate"),
            String(value, "credits"),
            boundaryOffset,
            boundary,
            headerStatus);
    }

    private static string? String(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("invalid format-native file metadata");
        return value.GetString();
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
/// <c>file_id = Merkle([content_root, identity_metadata_root])</c>.
///
/// The content node remains globally shared.  The metadata node is also ordinary content.
/// The file id composes the two, so identical text at two paths is one content tree but two
/// file occurrences.  The file floor is derived from its children; document roles need not mint wrappers.
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
        using var contentTree = ContentTierSpine.BuildTree(contentUtf8)
            ?? throw new InvalidOperationException("FileEntity.Resolve: content has no root");
        return Resolve(RootComponent(contentTree), metadata);
    }

    /// <summary>
    /// Resolve a file from an already-realized content root. Grammar and modality
    /// lanes supply their native root here rather than rebuilding text tiers.
    /// </summary>
    public static FileIdentity Resolve(
        in OrderedCompositionComponent contentRoot, in FileMetadata metadata)
    {
        using var metadataTree = ContentTierSpine.BuildTree(metadata.IdentityCanonicalUtf8())
            ?? throw new InvalidOperationException("FileEntity.Resolve: metadata has no root");
        return Compose(contentRoot, RootComponent(metadataTree), default);
    }

    /// <summary>
    /// Stage the ordered file parent and its metadata through the common native composer.
    /// Content is staged by the shared content pipeline; its identity remains reusable.
    /// </summary>
    public static FileIdentity Emit(
        SubstrateChangeBuilder builder,
        Hash128 parentSourceId,
        byte[] canonicalContent,
        in FileMetadata metadata)
    {
        using var contentTree = ContentTierSpine.BuildTree(canonicalContent)
            ?? throw new InvalidOperationException("FileEntity.Emit: content has no root");
        return Emit(builder, parentSourceId, RootComponent(contentTree), metadata);
    }

    /// <summary>
    /// Stage a containing file after the supplied content root has been staged by
    /// its owning native grammar or modality composer.
    /// </summary>
    public static FileIdentity Emit(
        SubstrateChangeBuilder builder,
        Hash128 parentSourceId,
        in OrderedCompositionComponent contentRoot,
        in FileMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(builder);
        using var metadataTree = ContentTierSpine.BuildTree(metadata.IdentityCanonicalUtf8())
            ?? throw new InvalidOperationException("FileEntity.Emit: metadata has no root");
        var identity = Compose(contentRoot, RootComponent(metadataTree), parentSourceId, builder.ContentStage);
        if (!ContentTierSpine.EmitTree(
                builder, metadataTree, identity.FileId, ReadOnlySpan<byte>.Empty, out var metadataRoot)
            || metadataRoot != identity.MetadataRootId)
            throw new InvalidOperationException("FileEntity.Emit: metadata staging changed its identity");
        return identity;
    }

    private static FileIdentity Compose(
        in OrderedCompositionComponent contentRoot,
        in OrderedCompositionComponent metadataRoot,
        Hash128 sourceId,
        IntentStage? stage = null)
    {
        var request = new OrderedCompositionRequest([contentRoot, metadataRoot],
            EntityTypeRegistry.SourceFile, sourceId, 0);
        OrderedCompositionResult result;
        if (stage is null)
            result = OrderedComposition.ComposeBatch([request])[0];
        else
        {
            Span<OrderedCompositionResult> results = stackalloc OrderedCompositionResult[1];
            OrderedComposition.StageBatch(stage, [request], results);
            result = results[0];
        }
        return new FileIdentity(contentRoot.Id, metadataRoot.Id, result.Id);
    }

    private static unsafe OrderedCompositionComponent RootComponent(TierTree tree)
    {
        if (tree.NodeCount == 0)
            throw new InvalidOperationException("file component has no canonical root");
        var node = tree.GetNode(tree.NaturalUnitIndex());
        return new(node.Id, node.Tier,
            node.Coord[0], node.Coord[1], node.Coord[2], node.Coord[3],
            node.Atom, node.Tier == 0);
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

}
