using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Semantic identity and provenance for one admitted source artifact.
///
/// The artifact is an opaque source reference, not generic user/document content. Its exact
/// byte fingerprint participates in identity; local path/mtime/size remain journal facts.
/// Source-format parsers attach their emitted changes to <see cref="ArtifactId"/> while the
/// source's recipe supplies the actual semantic claims.
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

    public static SubstrateChange BuildChange(
        IngestArtifact artifact,
        Hash128 sourceId,
        Hash128 trustClassId,
        Hash128? exactFingerprint = null,
        IReadOnlyList<Hash128>? requires = null)
    {
        SourceArtifactIdentity identity = Resolve(artifact, exactFingerprint);
        double trust = SourceTrust.ForClass(trustClassId);

        var builder = new SubstrateChangeBuilder(
            sourceId, $"artifact-provenance/{artifact.FileLabel}", null,
            entityCapacity: 12, physicalityCapacity: 0, attestationCapacity: 16)
            .DeclareSourcePrior(sourceId, trust);

        OrderedCompositionComponent sourceComponent =
            ContentEmitter.StageComponent(builder, artifact.Source, sourceId)
            ?? throw new InvalidOperationException("artifact source could not be composed");
        OrderedCompositionComponent releaseComponent =
            ContentEmitter.StageComponent(builder, artifact.Release, sourceId)
            ?? throw new InvalidOperationException("artifact release could not be composed");
        OrderedCompositionComponent artifactComponent =
            ContentEmitter.StageComponent(builder, artifact.Artifact, sourceId)
            ?? throw new InvalidOperationException("artifact name could not be composed");
        string exact = exactFingerprint is { } fingerprint
            ? fingerprint.ToString()
            : !string.IsNullOrWhiteSpace(artifact.Sha256)
                ? artifact.Sha256.Trim().ToLowerInvariant()
                : artifact.Id;
        OrderedCompositionComponent fingerprintComponent =
            ContentEmitter.StageComponent(builder, exact, sourceId)
            ?? throw new InvalidOperationException("artifact fingerprint could not be composed");

        Span<OrderedCompositionResult> composed = stackalloc OrderedCompositionResult[3];
        OrderedComposition.StageBatch(
            builder.ContentStage,
            [
                new OrderedCompositionRequest(
                    [sourceComponent, releaseComponent],
                    EntityTypeRegistry.SourceVersion, sourceId, 0),
                new OrderedCompositionRequest(
                    [sourceComponent, artifactComponent],
                    EntityTypeRegistry.SourceReference, sourceId, 0),
                new OrderedCompositionRequest(
                    [sourceComponent, releaseComponent, artifactComponent, fingerprintComponent],
                    EntityTypeRegistry.SourceReference, sourceId, 0),
            ],
            composed);

        if (composed[0].Id != identity.ReleaseId
            || composed[1].Id != identity.RoleId
            || composed[2].Id != identity.ArtifactId)
            throw new InvalidOperationException("source artifact identity changed during composition");

        builder.AddAttestation(NativeAttestation.Categorical(
            sourceId, "CONTAINS", identity.ArtifactId, sourceId, trust));
        builder.AddAttestation(NativeAttestation.Categorical(
            identity.ArtifactId, "HAS_VERSION", identity.ReleaseId, sourceId, trust));
        builder.AddAttestation(NativeAttestation.Categorical(
            identity.ArtifactId, "IS_TYPED_AS", identity.RoleId, sourceId, trust));

        if (requires is not null)
            foreach (Hash128 dependency in requires.Distinct())
                builder.AddAttestation(NativeAttestation.Categorical(
                    identity.ArtifactId, "REQUIRES", dependency, sourceId, trust));

        EmitText("HAS_SOURCE_URL", artifact.UpstreamUrl);
        EmitText("HAS_LICENSE", artifact.License);
        EmitText("HAS_CITATION", artifact.Citation);

        EmitProperty("sha256", artifact.Sha256);
        EmitProperty("upstream-checksum", artifact.UpstreamChecksum);
        EmitProperty("media-type", artifact.MediaType);
        EmitProperty("annotation-origin", artifact.AnnotationOrigin);
        EmitProperty("language", artifact.Language);
        EmitProperty("split", artifact.Split);

        SubstrateChange change = builder.Build();
        return Bind(change, identity.ArtifactId);

        void EmitText(string relation, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (ContentEmitter.Emit(builder, value, sourceId) is not { } root) return;
            builder.AddAttestation(NativeAttestation.Categorical(
                identity.ArtifactId, relation, root, sourceId, trust));
        }

        void EmitProperty(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Hash128 key = ContentEmitter.Emit(builder, name, sourceId)
                ?? throw new InvalidOperationException(
                    $"source artifact property name could not be composed: {name}");
            if (ContentEmitter.Emit(builder, value, sourceId) is not { } root) return;
            builder.AddAttestation(NativeAttestation.Categorical(
                identity.ArtifactId, "HAS_PROPERTY", root, sourceId, trust, contextId: key));
        }
    }

    private static Hash128 Root(string value) =>
        ContentEmitter.RootId(value)
        ?? throw new InvalidOperationException($"source artifact fragment could not be composed: {value}");

    private static Hash128 Merkle(params Hash128[] constituents) =>
        Hash128.Merkle(EntityTier.Document, constituents);

    public static Hash128 RecipeId(
        string sourceName, string release, string recipeName, IReadOnlyList<Hash128> requires)
    {
        string deps = string.Join(",", requires.Select(static id => id.ToString())
            .OrderBy(static id => id, StringComparer.Ordinal));
        return Hash128.OfCanonical(
            $"substrate/source_recipe/{sourceName}/{release}/{recipeName}/{deps}/v1");
    }

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
        double trust = SourceTrust.ForClass(trustClassId);
        Hash128 recipeId = RecipeId(sourceName, release, recipeName, requires);
        Hash128 roleId = Hash128.OfCanonical(
            $"substrate/source_recipe_role/{sourceName}/{recipeName}/v1");
        var builder = new SubstrateChangeBuilder(
            sourceId, $"source-recipe/{sourceName}/{recipeName}", null,
            entityCapacity: 4, physicalityCapacity: 0,
            attestationCapacity: 4 + requires.Count)
            .DeclareSourcePrior(sourceId, trust);
        builder.AddEntity(recipeId, EntityTier.Document, EntityTypeRegistry.SourceReference, sourceId);
        builder.AddEntity(roleId, EntityTier.Word, EntityTypeRegistry.SourceReference, sourceId);
        builder.AddAttestation(NativeAttestation.Categorical(
            sourceId, "CONTAINS", recipeId, sourceId, trust));
        builder.AddAttestation(NativeAttestation.Categorical(
            recipeId, "IS_TYPED_AS", roleId, sourceId, trust));
        foreach (Hash128 dependency in requires.Distinct())
            builder.AddAttestation(NativeAttestation.Categorical(
                recipeId, "REQUIRES", dependency, sourceId, trust));
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
