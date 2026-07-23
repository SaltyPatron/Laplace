using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Conversational turn witnessing with tenant/user/session provenance (spec 34).
///
/// Chess parity: a session is to conversation what a game is to chess — a
/// content-addressed context entity whose id rides on every turn's evidence rows
/// (context_id keeps per-session provenance; deduped subjects keep the fold shared).
/// Tenant identity lives in the SOURCE (`UserPrompt@{tenant}` / `Response@{tenant}`),
/// so two tenants asserting the same fact are distinct provenanced evidence rows by
/// construction, and `scoped_consensus(source_ids)` isolates a tenant's world with
/// zero extension changes. Tenant trust is the third witness-weight factor
/// (rank × source trust × tenant trust), default-neutral 1.0 — values are operator
/// policy, never invented here.
/// </summary>
public static class ConversationContent
{
    public const string PromptSourceBase = "UserPrompt";
    public const string ResponseSourceBase = "Response";

    public static readonly Hash128 SessionType = EntityTypeRegistry.ConversationSession;

    /// <summary>
    /// Tenant ids and session keys become canonical-key segments, and header tenants
    /// are attacker-controlled — the strict charset is load-bearing for key integrity.
    /// </summary>
    private static readonly Regex IdentifierPattern =
        new(@"^[A-Za-z0-9._@-]{1,128}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidIdentifier(string? value) =>
        value is not null && IdentifierPattern.IsMatch(value);

    /// <summary>Per-tenant witness identity: both sources plus the trust multiplier.</summary>
    public readonly record struct TenantScope(
        string Tenant,
        Hash128 PromptSource,
        Hash128 ResponseSource,
        double TenantTrust)
    {
        public string PromptSourceName => $"{PromptSourceBase}@{Tenant}";
        public string ResponseSourceName => $"{ResponseSourceBase}@{Tenant}";
    }

    public static TenantScope Resolve(string tenant, double tenantTrust = 1.0)
    {
        if (!IsValidIdentifier(tenant))
            throw new ArgumentException(
                $"tenant '{tenant}' is not a valid identifier ([A-Za-z0-9._@-]{{1,128}})", nameof(tenant));
        if (tenantTrust is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(tenantTrust), "tenant trust must be in [0,1]");
        return new TenantScope(
            tenant,
            SubstrateCanonicalIds.Source($"{PromptSourceBase}@{tenant}"),
            SubstrateCanonicalIds.Source($"{ResponseSourceBase}@{tenant}"),
            tenantTrust);
    }

    public static Hash128 SessionId(string tenant, string sessionKey)
    {
        if (!IsValidIdentifier(tenant))
            throw new ArgumentException($"tenant '{tenant}' is not a valid identifier", nameof(tenant));
        if (!IsValidIdentifier(sessionKey))
            throw new ArgumentException($"session key '{sessionKey}' is not a valid identifier", nameof(sessionKey));
        return SubstrateCanonicalIds.ConversationSession(tenant, sessionKey);
    }

    /// <summary>
    /// Every relation a source emits MUST be declared at bootstrap (the HAS_POS law);
    /// family expansion happens in the builder loop below.
    /// </summary>
    private static readonly string[] DeclaredRelations = ["APPEARS_IN", "PRECEDES", "HAS_ATTRIBUTION"];

    /// <summary>
    /// The three bootstrap changes for a tenant's first turn: prompt-source and
    /// response-source registrations (trust classes reuse the base conversational
    /// classes — the tenant changes WHO witnesses, not what KIND of witness it is),
    /// plus the source→tenant HAS_ATTRIBUTION linkage. Rows are idempotent; the
    /// witness lane caches per process so testimony refolds are bounded to restarts.
    /// </summary>
    public static SubstrateChange[] BuildTenantBootstrapChanges(TenantScope scope)
    {
        var promptBoot = new BootstrapIntentBuilder(
            scope.PromptSource, scope.PromptSourceName,
            SubstrateCanonicalIds.TrustClass("UserPromptContent"));
        var responseBoot = new BootstrapIntentBuilder(
            scope.ResponseSource, scope.ResponseSourceName,
            SubstrateCanonicalIds.TrustClass("ResponseContent"));
        foreach (var boot in new[] { promptBoot, responseBoot })
        {
            boot.AddType("Conversation_Session");
            foreach (var r in SourceVocabularyBootstrap.ExpandRelationsWithFamily(DeclaredRelations))
                boot.AddRelationType(r);
        }

        var attribution = new SubstrateChangeBuilder(
            scope.PromptSource, $"bootstrap/tenant/{scope.Tenant}", parentIntentId: null);
        if (ContentEmitter.Emit(attribution, scope.Tenant, scope.PromptSource) is { } tenantRoot)
        {
            attribution.AddAttestation(NativeAttestation.Categorical(
                scope.PromptSource, "HAS_ATTRIBUTION", tenantRoot,
                scope.PromptSource, null, SourceTrust.SubstrateMandate));
            attribution.AddAttestation(NativeAttestation.Categorical(
                scope.ResponseSource, "HAS_ATTRIBUTION", tenantRoot,
                scope.ResponseSource, null, SourceTrust.SubstrateMandate));
        }

        return [promptBoot.Build(), responseBoot.Build(), attribution.Build()];
    }

    /// <summary>
    /// One turn, one change, one apply (the writer's φ-per-cell invariant assumes a
    /// turn is never batched with another tenant's). Content lands via the ordinary
    /// text DAG mint; the loop-closing testimony is turn-level only — no per-token
    /// chains (Pillar 3a stays deleted):
    ///   (promptRoot APPEARS_IN session) @ctx=session   — record-lane membership
    ///   (replyRoot  APPEARS_IN session) @ctx=session   — record-lane membership
    ///   (promptRoot PRECEDES  replyRoot) @ctx=session  — the corroborating cell:
    ///     the same Q→A across sessions/tenants folds at one consensus cell while
    ///     evidence rows keep per-tenant/per-session provenance (the chess MOVE-edge
    ///     lesson).
    ///   (session HAS_ATTRIBUTION userRoot)             — once per session, when the
    ///     caller supplies a user key (user-within-tenant provenance).
    /// </summary>
    public static bool TryBuildTurnChange(
        TenantScope scope,
        Hash128 sessionId,
        byte[] promptUtf8,
        byte[]? replyUtf8,
        string? userKey,
        out SubstrateChange change,
        out Hash128 promptRoot,
        out Hash128 replyRoot)
    {
        change = default!;
        replyRoot = Hash128.Zero;

        double promptWeight = RelationTypeRank.Associative * SourceTrust.UserPrompt * scope.TenantTrust;
        if (!TextEntityBuilder.TryBuildContentWitness(promptUtf8, scope.PromptSource, promptWeight,
                out var pEntities, out var pPhysicalities, out _, out promptRoot, out _))
            return false;

        var rEntities = System.Collections.Immutable.ImmutableArray<EntityRow>.Empty;
        var rPhysicalities = System.Collections.Immutable.ImmutableArray<PhysicalityRow>.Empty;
        bool hasReply = false;
        if (replyUtf8 is { Length: > 0 })
        {
            double replyWeight = RelationTypeRank.Associative * SourceTrust.Response * scope.TenantTrust;
            hasReply = TextEntityBuilder.TryBuildContentWitness(replyUtf8, scope.ResponseSource, replyWeight,
                out rEntities, out rPhysicalities, out _, out replyRoot, out _);
        }

        var b = new SubstrateChangeBuilder(
            scope.PromptSource, "conversation/turn", parentIntentId: null,
            entityCapacity: pEntities.Length + rEntities.Length + 1,
            physicalityCapacity: pPhysicalities.Length + rPhysicalities.Length,
            attestationCapacity: 4);

        b.AddEntity(sessionId, EntityTier.Document, SessionType, scope.PromptSource);
        foreach (var e in pEntities) b.AddEntity(e);
        foreach (var p in pPhysicalities) b.AddPhysicality(p);
        foreach (var e in rEntities) b.AddEntity(e);
        foreach (var p in rPhysicalities) b.AddPhysicality(p);

        b.AddAttestation(NativeAttestation.Categorical(
            promptRoot, "APPEARS_IN", sessionId,
            scope.PromptSource, sessionId, SourceTrust.UserPrompt * scope.TenantTrust));

        if (hasReply)
        {
            b.AddAttestation(NativeAttestation.Categorical(
                replyRoot, "APPEARS_IN", sessionId,
                scope.ResponseSource, sessionId, SourceTrust.Response * scope.TenantTrust));
            b.AddAttestation(NativeAttestation.Categorical(
                promptRoot, "PRECEDES", replyRoot,
                scope.ResponseSource, sessionId, SourceTrust.Response * scope.TenantTrust));
        }

        if (userKey is not null)
        {
            if (!IsValidIdentifier(userKey))
                throw new ArgumentException($"user key '{userKey}' is not a valid identifier", nameof(userKey));
            if (ContentEmitter.Emit(b, userKey, scope.PromptSource) is { } userRoot)
                b.AddAttestation(NativeAttestation.Categorical(
                    sessionId, "HAS_ATTRIBUTION", userRoot,
                    scope.PromptSource, null, SourceTrust.UserPrompt * scope.TenantTrust));
        }

        change = b.Build();
        return true;
    }
}
