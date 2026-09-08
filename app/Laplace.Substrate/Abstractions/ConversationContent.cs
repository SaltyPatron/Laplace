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
/// construction. Tenant scope controls authorization and provenance; the seeded
/// source class owns the observation prior. Participant standing is distinct from
/// both, and is not an arbitrary multiplier assigned to a tenant (spec 34).
/// </summary>
public static class ConversationContent
{
    public enum TurnPhase { Complete, Input, Output }
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

    /// <summary>Per-tenant witness identities; authorization scope is not source trust.</summary>
    public readonly record struct TenantScope(
        string Tenant,
        Hash128 PromptSource,
        Hash128 ResponseSource)
    {
        public string PromptSourceName => $"{PromptSourceBase}@{Tenant}";
        public string ResponseSourceName => $"{ResponseSourceBase}@{Tenant}";
    }

    public static TenantScope Resolve(string tenant)
    {
        if (!IsValidIdentifier(tenant))
            throw new ArgumentException(
                $"tenant '{tenant}' is not a valid identifier ([A-Za-z0-9._@-]{{1,128}})", nameof(tenant));
        return new TenantScope(
            tenant,
            SubstrateCanonicalIds.Source($"{PromptSourceBase}@{tenant}"),
            SubstrateCanonicalIds.Source($"{ResponseSourceBase}@{tenant}"));
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
    private static readonly string[] DeclaredRelations =
        ["APPEARS_IN", "HAS_ATTRIBUTION", "HAS_ROLE", "IS_INSTANCE_OF", "DEPENDS_ON"];
    public static string MembershipRelation => DeclaredRelations[0];
    private static string AttributionRelation => DeclaredRelations[1];
    private static string RoleRelation => DeclaredRelations[2];
    private static string InstanceRelation => DeclaredRelations[3];
    private static string DependencyRelation => DeclaredRelations[4];

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
            boot.AddType("Conversation_Turn");
            boot.AddType("Conversation_Message");
            foreach (var r in SourceVocabularyBootstrap.ExpandRelationsWithFamily(DeclaredRelations))
                boot.AddRelationType(r);
        }

        var attribution = new SubstrateChangeBuilder(
            scope.PromptSource, $"bootstrap/tenant/{scope.Tenant}", parentIntentId: null);
        if (ContentEmitter.Emit(attribution, scope.Tenant, scope.PromptSource) is { } tenantRoot)
        {
            attribution.AddAttestation(NativeAttestation.Categorical(
                scope.PromptSource, AttributionRelation, tenantRoot,
                scope.PromptSource, null, SourceTrust.SubstrateMandate));
            attribution.AddAttestation(NativeAttestation.Categorical(
                scope.ResponseSource, AttributionRelation, tenantRoot,
                scope.ResponseSource, null, SourceTrust.SubstrateMandate));
        }

        return [promptBoot.Build(), responseBoot.Build(), attribution.Build()];
    }

    /// <summary>
    /// One turn, one change, one apply (the writer's φ-per-cell invariant assumes a
    /// turn is never batched with another tenant's). Content lands via the ordinary
    /// text DAG mint; the loop-closing testimony is turn-level only — no per-token
    /// chains (Pillar 3a stays deleted):
    ///   (messageOccurrence APPEARS_IN session) @ctx=session — record-lane membership
    ///   Each occurrence composes its metadata and exact content as separate branches.
    ///   Prompt/reply order is carried by the packed session trajectory appended
    ///     by the writer. It does not create a second PRECEDES consensus fact.
    ///   (session HAS_ATTRIBUTION userRoot)             — witnessed when the
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
        out Hash128 replyRoot,
        string? occurrenceKey = null,
        string? participantKey = null)
        => TryBuildTurnChange(scope, sessionId, promptUtf8, replyUtf8, userKey,
            out change, out promptRoot, out replyRoot, out _, occurrenceKey, participantKey);

    public static bool TryBuildTurnChange(
        TenantScope scope, Hash128 sessionId, byte[] promptUtf8, byte[]? replyUtf8,
        string? userKey, out SubstrateChange change, out Hash128 promptRoot,
        out Hash128 replyRoot, out Hash128[] turnIds, string? occurrenceKey = null,
        string? participantKey = null, TurnPhase phase = TurnPhase.Complete)
    {
        change = default!;
        turnIds = [];
        promptRoot = Hash128.Zero;
        replyRoot = Hash128.Zero;
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (phase != TurnPhase.Complete && occurrenceKey is null)
            throw new ArgumentException("A split turn requires its occurrence key.", nameof(occurrenceKey));
        if (occurrenceKey is not null && !IsValidIdentifier(occurrenceKey))
            throw new ArgumentException("Turn occurrence key is not a valid identifier.", nameof(occurrenceKey));
        participantKey ??= userKey;
        if (participantKey is not null && !IsValidIdentifier(participantKey))
            throw new ArgumentException("Participant key is not a valid identifier.", nameof(participantKey));
        if (userKey is not null && !IsValidIdentifier(userKey))
            throw new ArgumentException("User key is not a valid identifier.", nameof(userKey));
        occurrenceKey ??= Guid.NewGuid().ToString("N");

        if (phase == TurnPhase.Input && replyUtf8 is { Length: > 0 })
            throw new ArgumentException("Input admission cannot record a response.", nameof(replyUtf8));
        if (phase == TurnPhase.Output && replyUtf8 is not { Length: > 0 })
            return false;

        using var promptTree = ContentTierSpine.BuildTree(promptUtf8);
        if (promptTree is null || promptTree.NodeCount == 0)
            return false;
        promptRoot = promptTree.GetNode(promptTree.NaturalUnitIndex()).Id;

        using var replyTree = replyUtf8 is { Length: > 0 }
            ? ContentTierSpine.BuildTree(replyUtf8) : null;
        bool hasReply = replyUtf8 is { Length: > 0 };
        if (hasReply)
        {
            if (replyTree is null || replyTree.NodeCount == 0)
                return false;
            replyRoot = replyTree.GetNode(replyTree.NaturalUnitIndex()).Id;
        }

        var b = new SubstrateChangeBuilder(
            // Identical text in two actual turns is two occurrences. The writer
            // journals this change's intent, so a content-only unit label would
            // incorrectly suppress the second turn as a transport retry.
            phase == TurnPhase.Output ? scope.ResponseSource : scope.PromptSource,
            phase == TurnPhase.Complete
                ? $"conversation/turn/{sessionId}/{occurrenceKey}"
                : $"conversation/turn/{sessionId}/{occurrenceKey}/{phase}", parentIntentId: null);

        b.AddEntity(sessionId, EntityTier.Document, SessionType, scope.PromptSource);
        if ((phase != TurnPhase.Output && !ContentTierSpine.EmitTree(b, promptTree, scope.PromptSource, [], out _))
            || (hasReply && !ContentTierSpine.EmitTree(b, replyTree!, scope.ResponseSource, [], out _)))
            throw new InvalidOperationException("Conversation content could not be staged.");

        // The output names the exact input occurrence even when other requests
        // appended to this session during generation. Reconstructing its native
        // content identity does not submit the input testimony a second time.
        Hash128 promptTurn = StageOccurrence(b, scope, sessionId, occurrenceKey,
            "user", promptTree, scope.PromptSource,
            SourceTrust.UserPrompt, participantKey,
            stage: phase != TurnPhase.Output);
        if (phase != TurnPhase.Output)
        {
            turnIds = [promptTurn];
        }

        if (hasReply)
        {
            Hash128 replyTurn = StageOccurrence(b, scope, sessionId, occurrenceKey,
                "assistant", replyTree!, scope.ResponseSource,
                SourceTrust.Response);
            b.AddAttestation(NativeAttestation.Categorical(
                replyTurn, DependencyRelation, promptTurn, scope.ResponseSource,
                sessionId, SourceTrust.Response));
            turnIds = [.. turnIds, replyTurn];
        }

        if (userKey is not null && phase != TurnPhase.Output)
        {
            if (ContentEmitter.Emit(b, userKey, scope.PromptSource) is { } userRoot)
                b.AddAttestation(NativeAttestation.Categorical(
                    sessionId, AttributionRelation, userRoot,
                    scope.PromptSource, null, SourceTrust.UserPrompt));
        }

        change = b.Build();
        return true;
    }

    // Spec 34: an occurrence and its message content have separate identities.
    // The metadata branch prevents identical text in different roles/sessions
    // from sharing a role-bearing subject; the content branch stays unchanged.
    private static unsafe Hash128 StageOccurrence(
        SubstrateChangeBuilder builder, TenantScope scope, Hash128 sessionId,
        string occurrenceKey, string role, TierTree contentTree,
        Hash128 sourceId, double trust, string? participantKey = null, bool stage = true)
    {
        byte[] metadata = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "conversation-message-v1", tenant = scope.Tenant,
            session = sessionId.ToString(), occurrence = occurrenceKey, role,
            source = sourceId.ToString(), participant = participantKey
        });
        using var metadataTree = ContentTierSpine.BuildTree(metadata)
            ?? throw new InvalidOperationException("Conversation metadata could not be composed.");
        var metadataRoot = metadataTree.GetNode(metadataTree.NaturalUnitIndex());
        var contentRoot = contentTree.GetNode(contentTree.NaturalUnitIndex());
        Hash128[] members = [metadataRoot.Id, contentRoot.Id];
        double[] coords = new double[8];
        for (int axis = 0; axis < 4; ++axis)
        {
            coords[axis] = metadataRoot.Coord[axis];
            coords[4 + axis] = contentRoot.Coord[axis];
        }
        byte tier = checked((byte)(Math.Max(metadataRoot.Tier, contentRoot.Tier) + 1));
        Span<double> centroid = stackalloc double[4];
        var (id, hilbert) = HashComposer.ComposeNode(tier, members, coords, centroid);
        if (!stage) return id;
        if (!ContentTierSpine.EmitTree(builder, metadataTree, sourceId, [], out _))
            throw new InvalidOperationException("Conversation metadata could not be staged.");
        ulong[] flags =
        [
            Trajectory.VertexFlags(metadataRoot.Tier, metadataRoot.Tier == 0, metadataRoot.Atom),
            Trajectory.VertexFlags(contentRoot.Tier, contentRoot.Tier == 0, contentRoot.Atom)
        ];
        builder.AddEntity(id, tier, EntityTypeRegistry.ConversationMessage, sourceId);
        builder.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(id, PhysicalityType.Content), id, sourceId,
            PhysicalityType.Content, centroid[0], centroid[1], centroid[2], centroid[3],
            hilbert, Trajectory.Build(members, flags), members.Length, null, null,
            IngestClock.NowUnixUs()));
        Hash128 roleId = Hash128.OfCanonical($"agent/role/{role}/v1");
        builder.AddEntity(roleId, EntityTier.Word, EntityTypeRegistry.ConversationTurn, sourceId);
        Hash128 roleName = ContentEmitter.Emit(builder, role, sourceId)
            ?? throw new InvalidOperationException("Conversation role could not be composed.");
        builder.AddAttestation(NativeAttestation.Categorical(
            roleId, InstanceRelation, roleName, sourceId, sessionId, trust));
        builder.AddAttestation(NativeAttestation.Categorical(
            id, RoleRelation, roleId, sourceId, sessionId, trust));
        builder.AddAttestation(NativeAttestation.Categorical(
            id, MembershipRelation, sessionId, sourceId, sessionId, trust));
        if (participantKey is not null)
        {
            Hash128 participant = ContentEmitter.Emit(builder, participantKey, sourceId)
                ?? throw new InvalidOperationException("Conversation participant could not be composed.");
            builder.AddAttestation(NativeAttestation.Categorical(
                id, AttributionRelation, participant, sourceId, sessionId, trust));
        }
        return id;
    }
}
