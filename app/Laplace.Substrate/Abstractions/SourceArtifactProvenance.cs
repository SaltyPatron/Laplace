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
        string exact = exactFingerprint is { } fingerprint
            ? fingerprint.ToString()
            : !string.IsNullOrWhiteSpace(artifact.Sha256)
                ? artifact.Sha256.Trim().ToLowerInvariant()
                : artifact.Id;
        return new(
            Hash128.OfCanonical(
                $"substrate/source_artifact/{artifact.Source}/{artifact.Release}/{artifact.Artifact}/{exact}/v1"),
            Hash128.OfCanonical(
                $"substrate/source_release/{artifact.Source}/{artifact.Release}/v1"),
            Hash128.OfCanonical(
                $"substrate/source_artifact_role/{artifact.Source}/{artifact.Artifact}/v1"));
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

        builder.AddEntity(
            identity.ArtifactId, EntityTier.Document,
            EntityTypeRegistry.SourceReference, sourceId);
        builder.AddEntity(
            identity.ReleaseId, EntityTier.Word,
            EntityTypeRegistry.SourceVersion, sourceId);
        builder.AddEntity(
            identity.RoleId, EntityTier.Word,
            EntityTypeRegistry.SourceReference, sourceId);

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
            Hash128 key = Hash128.OfCanonical($"substrate/source_artifact_property/{name}/v1");
            builder.AddEntity(
                key, EntityTier.Word, EntityTypeRegistry.SourceReference, sourceId);
            if (ContentEmitter.Emit(builder, value, sourceId) is not { } root) return;
            builder.AddAttestation(NativeAttestation.Categorical(
                identity.ArtifactId, "HAS_PROPERTY", root, sourceId, trust, contextId: key));
        }
    }

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
