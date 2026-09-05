using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Runtime user-owned file/document content. Content identity stays global; ownership and
/// provenance live above it in the source trunk:
///
/// <code>
/// UserContent@tenant --CONTAINS--> file --CONTAINS--> document -> content DAG
/// </code>
///
/// Therefore a user's document can collide by content with a seeded book without becoming
/// seeded content or minting a duplicate content id. The source/file/document chain answers
/// who supplied this occurrence; the content root answers what bytes it contains.
/// </summary>
public static class UserArtifactContent
{
    public const string SourceBase = "UserContent";

    private static readonly string[] DeclaredRelations = ["CONTAINS", "HAS_ATTRIBUTION"];
    public static string MembershipRelation => DeclaredRelations[0];
    public static string AttributionRelation => DeclaredRelations[1];

    public readonly record struct TenantScope(
        string Tenant,
        Hash128 Source,
        double TenantTrust)
    {
        public string SourceName => $"{SourceBase}@{Tenant}";
    }

    public readonly record struct ArtifactIds(
        Hash128 FileId,
        Hash128 DocumentId,
        Hash128 ContentId,
        Hash128 MetadataId,
        Hash128 SourceId);

    public static TenantScope Resolve(string tenant, double tenantTrust = 1.0)
    {
        if (!ConversationContent.IsValidIdentifier(tenant))
            throw new ArgumentException(
                $"tenant '{tenant}' is not a valid identifier ([A-Za-z0-9._@-]{{1,128}})", nameof(tenant));
        if (tenantTrust is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(tenantTrust), "tenant trust must be in [0,1]");
        return new TenantScope(
            tenant,
            SubstrateCanonicalIds.Source($"{SourceBase}@{tenant}"),
            tenantTrust);
    }

    public static SubstrateChange[] BuildTenantBootstrapChanges(TenantScope scope)
    {
        var boot = new BootstrapIntentBuilder(
            scope.Source,
            scope.SourceName,
            SubstrateCanonicalIds.TrustClass("UserPromptContent"));
        boot.AddType("SourceFile");
        boot.AddType("Document");
        foreach (var relation in SourceVocabularyBootstrap.ExpandRelationsWithFamily(DeclaredRelations))
            boot.AddRelationType(relation);

        var attribution = new SubstrateChangeBuilder(
            scope.Source, $"bootstrap/user-content/{scope.Tenant}", parentIntentId: null);
        if (ContentEmitter.Emit(attribution, scope.Tenant, scope.Source) is { } tenantRoot)
            attribution.AddAttestation(NativeAttestation.Categorical(
                scope.Source, AttributionRelation, tenantRoot,
                scope.Source, null, SourceTrust.SubstrateMandate));

        return [boot.Build(), attribution.Build()];
    }

    public static bool TryBuildTextArtifactChange(
        TenantScope scope,
        string name,
        string relativePath,
        byte[] contentUtf8,
        string? userKey,
        DateTime? modifiedUtc,
        out SubstrateChange change,
        out ArtifactIds ids)
    {
        change = default!;
        ids = default;
        if (contentUtf8.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("artifact name is required", nameof(name));
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("artifact relative path is required", nameof(relativePath));

        var metadata = new FileMetadata(
            name, NormalizeRelativePath(relativePath),
            contentUtf8.LongLength, modifiedUtc?.ToUniversalTime() ?? DateTime.UnixEpoch);
        FileIdentity file = FileEntity.Resolve(contentUtf8, metadata);
        Hash128 documentId = file.ContentRootId;

        var builder = new SubstrateChangeBuilder(
            scope.Source,
            $"user-content/{scope.Tenant}/{metadata.RelativePath}",
            parentIntentId: null);

        if (!ContentTierSpine.TryStageIntoBuilder(
                builder, contentUtf8, documentId, out var emittedContent)
            || emittedContent != file.ContentRootId)
            return false;

        FileIdentity emittedFile = FileEntity.Emit(
            builder, scope.Source, contentUtf8, metadata);
        if (emittedFile != file)
            throw new InvalidOperationException("user artifact file identity changed during compose");

        double weight = RelationTypeRank.Associative * SourceTrust.UserPrompt * scope.TenantTrust;
        builder.AddAttestation(NativeAttestation.Categorical(
            scope.Source, MembershipRelation, file.FileId,
            scope.Source, null, weight));

        if (!string.IsNullOrWhiteSpace(userKey))
        {
            if (!ConversationContent.IsValidIdentifier(userKey))
                throw new ArgumentException($"user key '{userKey}' is not a valid identifier", nameof(userKey));
            if (ContentEmitter.Emit(builder, userKey, scope.Source) is { } userRoot)
                builder.AddAttestation(NativeAttestation.Categorical(
                    file.FileId, AttributionRelation, userRoot,
                    scope.Source, null, weight));
        }

        change = builder.Build();
        ids = new ArtifactIds(
            file.FileId, documentId, file.ContentRootId, file.MetadataRootId, scope.Source);
        return true;
    }

    public static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');
}
