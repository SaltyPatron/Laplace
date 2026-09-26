using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Journal identity for one admitted source artifact: completion and resume key on it.
/// It is never staged as content or testimony; the source's recipe supplies the claims.
/// </summary>
public readonly record struct SourceArtifactIdentity(
    Hash128 ArtifactId,
    Hash128 ReleaseId,
    Hash128 RoleId);

public static class SourceArtifactProvenance
{
    public static SourceArtifactIdentity Resolve(
        IngestArtifact artifact,
        Hash128? exactFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        string exact = exactFingerprint is { } exactHash
            ? exactHash.ToString()
            : !string.IsNullOrWhiteSpace(artifact.Sha256)
                ? artifact.Sha256.Trim().ToLowerInvariant()
                : artifact.Id;
        Hash128 source = Root(artifact.Source);
        Hash128 release = Root(artifact.Release);
        Hash128 artifactName = Root(artifact.Artifact);
        Hash128 fingerprint = Root(exact);
        return new(
            Merkle(source, release, artifactName, fingerprint),
            Merkle(source, release),
            Merkle(source, artifactName));
    }

    /// <summary>
    /// The artifact's journal binding. An artifact occurrence (path, digest, media type,
    /// the decomposer that read it) is provenance for completion and resume, not content
    /// and not a claim: nothing is staged, the change only carries the artifact binding.
    /// </summary>
    public static SubstrateChange BuildChange(
        IngestArtifact artifact,
        Hash128 sourceId,
        Hash128 trustClassId,
        Hash128? exactFingerprint = null,
        IReadOnlyList<Hash128>? requires = null)
    {
        SourceArtifactIdentity identity = Resolve(artifact, exactFingerprint);
        var builder = new SubstrateChangeBuilder(
            sourceId, $"artifact-provenance/{artifact.FileLabel}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0);
        return Bind(builder.Build(), identity.ArtifactId);
    }

    /// <summary>
    /// A file's metadata tree: [its path within the release, its SHA-256], both content.
    /// It heads the file's trunk, whose trajectory then lists every content the file states;
    /// a zip entry is its own file, named artifact!entry.
    /// </summary>
    public static string FilePath(IngestArtifact artifact, string? entry = null) =>
        entry is null ? artifact.RelativePath : $"{artifact.RelativePath}!{entry}";

    public static Hash128 FileMetadataId(IngestArtifact artifact, string? entry = null) =>
        Merkle(Root(FilePath(artifact, entry)), Root(Digest(artifact)));

    public static OrderedCompositionComponent StageFileMetadata(
        SubstrateChangeBuilder builder, IngestArtifact artifact, string? entry, Hash128 sourceId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Hash128 id = FileMetadataId(artifact, entry);
        OrderedCompositionComponent path = ContentEmitter.StageComponent(builder, FilePath(artifact, entry), sourceId)
            ?? throw new InvalidOperationException($"file path could not be composed: {artifact.RelativePath}");
        OrderedCompositionComponent digest = ContentEmitter.StageComponent(builder, Digest(artifact), sourceId)
            ?? throw new InvalidOperationException($"file digest could not be composed: {artifact.RelativePath}");
        Span<OrderedCompositionResult> composed = stackalloc OrderedCompositionResult[1];
        OrderedComposition.StageBatch(builder.ContentStage,
            [new OrderedCompositionRequest([path, digest], EntityTypeRegistry.Id("Source_Reference"), sourceId, 0)],
            composed);
        if (composed[0].Id != id)
            throw new InvalidOperationException("file metadata identity changed during composition");
        OrderedCompositionResult r = composed[0];
        return new OrderedCompositionComponent(r.Id, r.Tier, r.CoordX, r.CoordY, r.CoordZ, r.CoordM);
    }

    private static string Digest(IngestArtifact artifact) =>
        string.IsNullOrWhiteSpace(artifact.Sha256)
            ? throw new InvalidDataException($"artifact {artifact.RelativePath} has no SHA-256")
            : artifact.Sha256.Trim().ToLowerInvariant();

    private static Hash128 Root(string value) =>
        ContentEmitter.RootId(value)
        ?? throw new InvalidOperationException($"source artifact fragment could not be composed: {value}");

    private static Hash128 Merkle(params Hash128[] constituents) =>
        Hash128.Merkle(EntityTier.Document, constituents);

    public static Hash128 RecipeId(
        string sourceName, string release, string recipeName, IReadOnlyList<Hash128> requires)
    {
        ArgumentNullException.ThrowIfNull(requires);
        return Merkle(Root(sourceName), Root(release), Root(recipeName));
    }

    /// <summary>The recipe execution's journal binding; nothing is staged (see BuildChange).</summary>
    public static SubstrateChange BuildRecipeChange(
        string sourceName,
        string release,
        string recipeName,
        Hash128 sourceId,
        Hash128 trustClassId,
        IReadOnlyList<Hash128> requires)
    {
        if (requires.Count == 0)
            throw new ArgumentException("a source recipe must declare at least one input artifact", nameof(requires));
        var builder = new SubstrateChangeBuilder(
            sourceId, $"source-recipe/{sourceName}/{recipeName}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0);
        return builder.Build() with { CountsAsUnit = false };
    }

    public static SubstrateChange Bind(SubstrateChange change, Hash128 artifactId)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Metadata.FileId is { } existing && existing != artifactId)
            throw new InvalidOperationException(
                $"ingest change already carries semantic file/artifact identity {existing}; "
                + $"cannot bind source artifact {artifactId}");
        return change with { Metadata = change.Metadata with { FileId = artifactId } };
    }
}
