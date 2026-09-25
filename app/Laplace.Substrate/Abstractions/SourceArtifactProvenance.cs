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
