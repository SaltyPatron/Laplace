using System.Globalization;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// The one compose path for a normalized <see cref="AgentSession"/> (spec 34 identity
/// hierarchy, batch counterpart of TurnCloser):
///
///   part text     → tiered content DAG (TextEntityBuilder), same roots as live turns
///   turn          → ordered composition of its part roots: merkle id + Content
///                   physicality whose trajectory IS the part order (Pillar 3a)
///   tool call     → Tool_Invocation composition of input/result roots, CALLS / HAS_INPUT
///                   / HAS_RESULT edges
///   session       → stable canonical identity (tenant=provider, key=session id) whose
///                   Content physicality trajectory is the ordered turn manifest —
///                   re-ingesting a grown log upserts the trajectory (versioned order)
///
/// Membership/PRECEDES ride the per-tenant UserPrompt@/Response@ sources so replayed
/// logs fold onto the SAME consensus cells as live conversation; structure, usage
/// scalars and retained metadata ride the AgentTrace lane source. Every attestation
/// and composed physicality carries the LOG's event time, not ingest time.
/// </summary>
public static class AgentTraceEmitter
{
    private static readonly Hash128 LaneSource = AgentTraceSource.SourceId;

    /// <summary>Typed relation → canonical surface (no ad-hoc name literals at emit sites).</summary>
    private static string Rel(AgentRelation relation) => AgentRelations.Surface(relation);

    /// <summary>Per-tenant witness identities, resolved once per provider per process.</summary>
    public readonly record struct ProviderScope(
        ConversationContent.TenantScope Tenant,
        Hash128 ToolSource)
    {
        public static ProviderScope Resolve(string provider) => new(
            ConversationContent.Resolve(provider),
            SubstrateCanonicalIds.Source($"ToolResult@{provider}"));
    }

    // ── grown-log re-witness protection ───────────────────────────────────────────
    // A resumed session file GROWS, so its per-file content identity changes and file
    // resume cannot skip it — without a finer marker every re-ingest would re-witness
    // the whole prefix and inflate observation counts (the #417 novelty-gate class).
    // The marker is a bare content-addressed entity per witnessed turn-prefix:
    //   id = canonical(agent/watermark/{session}/{k}/{chain_k})
    // where chain_k folds the ordered composed-turn ids, so ANY change inside the
    // prefix (not just its tail) invalidates deeper marks. The extract stage probes all
    // k in ONE batched existence bitmap; a probe miss merely re-witnesses (safe), a hit
    // is only possible for a byte-identical prefix.

    /// <summary>Part texts of a turn in THE composition order (the turn-id contract).</summary>
    internal static IEnumerable<string> PartTexts(AgentTurn turn)
    {
        if (!string.IsNullOrEmpty(turn.Text)) yield return turn.Text;
        if (!string.IsNullOrEmpty(turn.Thinking)) yield return turn.Thinking;
        foreach (var call in turn.ToolCalls)
        {
            if (!string.IsNullOrEmpty(call.InputJson)) yield return call.InputJson;
            if (!string.IsNullOrEmpty(call.ResultText)) yield return call.ResultText;
        }
    }

    /// <summary>
    /// Composed-turn ids by pure content resolution (no staging) — the same merkle the
    /// witness path mints, for watermark probing before compose.
    /// </summary>
    public static List<Hash128> ComputeComposedTurnIds(AgentSession session)
    {
        var ids = new List<Hash128>(session.Turns.Count);
        var members = new List<Hash128>(4);
        foreach (var turn in session.Turns)
        {
            members.Clear();
            foreach (var text in PartTexts(turn))
                if (ContentTierSpine.ResolveRoot(text) is { } root)
                    members.Add(root);
            if (members.Count == 0) continue;
            ids.Add(members.Count == 1
                ? members[0]
                : Hash128.Merkle(EntityTier.Document, System.Runtime.InteropServices
                    .CollectionsMarshal.AsSpan(members)));
        }
        return ids;
    }

    internal static Hash128 WatermarkChainStep(Hash128 chain, Hash128 turnId)
    {
        Span<byte> buf = stackalloc byte[32];
        chain.WriteBytes(buf[..16]);
        turnId.WriteBytes(buf[16..]);
        return Hash128.Blake3(buf);
    }

    internal static Hash128 WatermarkId(Hash128 sessionId, int k, Hash128 chain) =>
        Hash128.OfCanonical($"agent/watermark/{sessionId}/{k}/{chain}/v1");

    /// <summary>Candidate watermark ids for every prefix k = 1..N, in prefix order.</summary>
    public static IReadOnlyList<Hash128> WatermarkCandidates(
        Hash128 sessionId, IReadOnlyList<Hash128> composedTurnIds)
    {
        var candidates = new Hash128[composedTurnIds.Count];
        Hash128 chain = sessionId;
        for (int k = 0; k < composedTurnIds.Count; k++)
        {
            chain = WatermarkChainStep(chain, composedTurnIds[k]);
            candidates[k] = WatermarkId(sessionId, k + 1, chain);
        }
        return candidates;
    }

    public static void Emit(SubstrateChangeBuilder b, AgentSession session)
    {
        var scope = ProviderScope.Resolve(session.Provider);
        Hash128 sessionId = ConversationContent.SessionId(
            session.Provider, SanitizeKey(session.SessionKey));
        long sessionUs = session.StartedAtUnixUs;
        int watermark = session.WitnessedTurnWatermark;

        b.AddEntity(sessionId, EntityTier.Document, EntityTypeRegistry.ConversationSession,
            scope.Tenant.PromptSource);

        var coords = new Dictionary<Hash128, double[]>();
        var turnIds = new List<Hash128>(session.Turns.Count);
        var turnCoords = new List<double[]>(session.Turns.Count);
        long turnsUsedUs = 0;
        Hash128? lastUserTextRoot = null;
        var totals = new UsageTotals();

        foreach (var turn in session.Turns)
        {
            long ts = turn.TimestampUnixUs > 0 ? turn.TimestampUnixUs : sessionUs;
            Hash128 roleSource = turn.Role switch
            {
                AgentRoles.Assistant => scope.Tenant.ResponseSource,
                AgentRoles.Tool => scope.ToolSource,
                _ => scope.Tenant.PromptSource,
            };

            var members = new List<Hash128>(4);
            Hash128? textRoot = Witness(b, turn.Text, roleSource, coords, members);
            Witness(b, turn.Thinking, scope.Tenant.ResponseSource, coords, members);

            var invocations = new List<(AgentToolCall Call, Hash128? Id, Hash128? Input, Hash128? Result)>();
            foreach (var call in turn.ToolCalls)
            {
                Hash128? inputRoot = Witness(b, call.InputJson, scope.Tenant.ResponseSource, coords, members);
                Hash128? resultRoot = Witness(b, call.ResultText, scope.ToolSource, coords, members);
                Hash128? invocationId = ComposeOrdered(
                    b, Roots(inputRoot, resultRoot), EntityTier.Document,
                    EntityTypeRegistry.ToolInvocation, LaneSource, coords,
                    call.TimestampUnixUs > 0 ? call.TimestampUnixUs : ts);
                invocations.Add((call, invocationId, inputRoot, resultRoot));
            }

            Hash128? turnId = ComposeOrdered(
                b, members, EntityTier.Document, EntityTypeRegistry.ConversationTurn,
                roleSource, coords, ts);
            if (turnId is not { } tid) continue;

            turnIds.Add(tid);
            turnCoords.Add(coords[tid]);
            turnsUsedUs = Math.Max(turnsUsedUs, ts);

            // Prefix turns of a grown log: content stays staged (idempotent), testimony
            // is NOT re-emitted — the prior ingest already witnessed it, and testimony
            // is not idempotent (observation counts accumulate).
            bool witnessTurn = turnIds.Count > watermark;
            if (!witnessTurn)
            {
                if (turn.Usage is { IsEmpty: false } priorUsage) totals.Add(priorUsage);
                // Q→A pairing state advances through the prefix: a skipped assistant
                // turn consumed its prompt (that pair was witnessed by the prior run).
                if (turn.Role == AgentRoles.User) lastUserTextRoot = textRoot;
                else if (turn.Role == AgentRoles.Assistant) lastUserTextRoot = null;
                continue;
            }

            // Membership on the live lane's cell: (turn root APPEARS_IN session)@ctx=session.
            // ONE witness class (φ) for every membership row: content collapse means the
            // same subject can be a user turn, an assistant turn, and a tool input in one
            // batch, and the fold's φ-per-cell invariant forbids role-varying trust on one
            // cell (proven by the ~/.claude corpus: 12 workers died on exactly that).
            // Role identity stays on the evidence row's SOURCE and on HAS_ROLE.
            Attest(b, ts, NativeAttestation.Categorical(
                tid, Rel(AgentRelation.AppearsIn), sessionId, roleSource, sessionId,
                TC.AppDerived * scope.Tenant.TenantTrust));

            // Role/model/stop-reason/usage: lane-source structure, session as context.
            AttestCanonical(b, ts, tid, Rel(AgentRelation.HasRole),
                CanonicalEntity(b, $"agent/role/{turn.Role}/v1", turn.Role,
                    EntityTypeRegistry.ConversationTurn, coords: null),
                sessionId);
            if (IsRealModelId(turn.Model))
                AttestCanonical(b, ts, tid, Rel(AgentRelation.AuthoredBy),
                    CanonicalEntity(b, $"agent/model/{turn.Model}/v1", turn.Model!,
                        EntityTypeRegistry.AgentModel, coords: null),
                    sessionId);
            if (!string.IsNullOrEmpty(turn.StopReason))
                AttestCanonical(b, ts, tid, Rel(AgentRelation.HasStopReason),
                    CanonicalEntity(b, $"agent/stop/{SanitizeKey(turn.StopReason)}/v1",
                        turn.StopReason, EntityTypeRegistry.ConversationTurn, coords: null),
                    sessionId);

            if (turn.Usage is { IsEmpty: false } usage)
            {
                totals.Add(usage);
                EmitUsage(b, ts, tid, sessionId, usage, coords);
            }

            foreach (var (call, invocationId, inputRoot, resultRoot) in invocations)
            {
                Hash128 toolEntity = CanonicalEntity(
                    b, $"agent/tool/{SanitizeKey(call.Name)}/v1", call.Name,
                    EntityTypeRegistry.AgentTool, coords: null);
                Attest(b, ts, NativeAttestation.Categorical(
                    tid, Rel(AgentRelation.Calls), toolEntity, LaneSource, sessionId, TC.AppDerived));
                if (invocationId is not { } inv) continue;
                Attest(b, ts, NativeAttestation.Categorical(
                    inv, Rel(AgentRelation.IsInstanceOf), toolEntity, LaneSource, sessionId, TC.AppDerived));
                Attest(b, ts, NativeAttestation.Categorical(
                    inv, Rel(AgentRelation.AppearsIn), sessionId, LaneSource, sessionId, TC.AppDerived));
                if (inputRoot is { } ir && ir != inv)
                    Attest(b, ts, NativeAttestation.Categorical(
                        inv, Rel(AgentRelation.HasInput), ir, LaneSource, sessionId, TC.AppDerived));
                if (resultRoot is { } rr && rr != inv)
                    Attest(b, ts, NativeAttestation.Categorical(
                        inv, Rel(AgentRelation.HasResult), rr, LaneSource, sessionId, TC.AppDerived));
                if (call.IsError)
                    AttestMetaAttribute(b, ts, inv, sessionId, "is_error", "true", coords);
            }

            foreach (var (k, v) in turn.Meta)
                AttestMetaAttribute(b, ts, tid, sessionId, k, v, coords);

            // The live lane's corroborating cell: prompt root PRECEDES the reply root that
            // answered it — cross-session/tenant Q→A consensus (ConversationContent parity).
            // NOT a per-adjacency chain; order lives in the session trajectory.
            if (turn.Role == AgentRoles.User) lastUserTextRoot = textRoot;
            else if (turn.Role == AgentRoles.Assistant && textRoot is { } reply
                     && lastUserTextRoot is { } promptRoot && promptRoot != reply)
            {
                Attest(b, ts, NativeAttestation.Categorical(
                    promptRoot, Rel(AgentRelation.Precedes), reply, scope.Tenant.ResponseSource, sessionId,
                    TC.Response * scope.Tenant.TenantTrust));
                lastUserTextRoot = null;
            }
        }

        long endUs = session.EndedAtUnixUs > 0 ? session.EndedAtUnixUs : turnsUsedUs;

        // The session's ordered, versioned turn manifest — THE order authority (spec 34).
        if (turnIds.Count > 0)
        {
            var flat = new double[turnIds.Count * 4];
            for (int i = 0; i < turnIds.Count; i++) turnCoords[i].CopyTo(flat, i * 4);
            double[] centroid = Math4d.KarcherMean(flat);
            Hash128 physId = PhysicalityId.Compute(sessionId, PhysicalityType.Content);
            if (b.TrySeePhysicality(physId))
                b.AddPhysicalityPreSeen(new PhysicalityRow(
                    Id: physId, EntityId: sessionId, SourceId: scope.Tenant.PromptSource,
                    Type: PhysicalityType.Content,
                    CoordX: centroid[0], CoordY: centroid[1],
                    CoordZ: centroid[2], CoordM: centroid[3],
                    HilbertIndex: Hilbert128.Encode(centroid),
                    TrajectoryXyzm: Trajectory.Build(System.Runtime.InteropServices.CollectionsMarshal
                        .AsSpan(turnIds)),
                    NConstituents: turnIds.Count,
                    AlignmentResidual: null, SourceDim: null,
                    ObservedAtUnixUs: endUs));
        }

        // The witnessed-prefix watermark for THIS parse, atomically with its testimony.
        // A future re-ingest of the grown file probes these and skips the prefix.
        if (turnIds.Count > 0)
        {
            Hash128 chain = sessionId;
            foreach (var tid in turnIds) chain = WatermarkChainStep(chain, tid);
            b.AddEntity(WatermarkId(sessionId, turnIds.Count, chain), EntityTier.Word,
                EntityTypeRegistry.AgentSessionWatermark, LaneSource);
        }

        // A re-parse with NOTHING beyond the watermark owes no session-level testimony
        // either — it was all witnessed by the run that deposited the watermark.
        if (turnIds.Count <= watermark) return;

        // Session-level metadata — every field the log carried.
        if (Witness(b, session.Title, scope.Tenant.PromptSource, coords, members: null) is { } title)
            Attest(b, endUs, NativeAttestation.Categorical(
                sessionId, Rel(AgentRelation.HasName), title, LaneSource, sessionId, TC.AppDerived));
        if (Witness(b, session.Cwd, scope.Tenant.PromptSource, coords, members: null) is { } cwd)
            Attest(b, endUs, NativeAttestation.Categorical(
                sessionId, Rel(AgentRelation.HasContext), cwd, LaneSource, sessionId, TC.AppDerived));
        if (session.GitBranch is { Length: > 0 } branch)
            AttestMetaAttribute(b, endUs, sessionId, sessionId, "gitBranch", branch, coords);
        if (session.UserKey is { Length: > 0 } user
            && Witness(b, user, scope.Tenant.PromptSource, coords, members: null) is { } userRoot)
            Attest(b, endUs, NativeAttestation.Categorical(
                sessionId, Rel(AgentRelation.HasAttribution), userRoot, scope.Tenant.PromptSource, null,
                TC.UserPrompt * scope.Tenant.TenantTrust));
        if (sessionUs > 0)
        {
            string date = DateTimeOffset.FromUnixTimeMilliseconds(sessionUs / 1000)
                .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (Witness(b, date, scope.Tenant.PromptSource, coords, members: null) is { } dateRoot)
                Attest(b, endUs, NativeAttestation.Categorical(
                    sessionId, Rel(AgentRelation.OnDate), dateRoot, LaneSource, sessionId, TC.AppDerived));
        }
        foreach (var (k, v) in session.Meta)
            AttestMetaAttribute(b, endUs, sessionId, sessionId, k, v, coords);
        if (!totals.IsEmpty)
            EmitUsage(b, endUs, sessionId, sessionId, totals.ToUsage(), coords);
    }

    // ── content witnessing ────────────────────────────────────────────────────────

    /// <summary>
    /// Stage the tiered content DAG for one part; returns its root and records the root
    /// coordinate. Appends to <paramref name="members"/> (composition order) when given.
    /// </summary>
    private static Hash128? Witness(
        SubstrateChangeBuilder b, string? text, Hash128 sourceId,
        Dictionary<Hash128, double[]> coords, List<Hash128>? members)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (!TextEntityBuilder.TryBuildContentWitness(
                Encoding.UTF8.GetBytes(text), sourceId, 1.0,
                out var entities, out var physicalities, out _, out var root, out _))
            return null;
        foreach (var e in entities) b.AddEntity(e);
        foreach (var p in physicalities)
        {
            b.AddPhysicality(p);
            if (!coords.ContainsKey(p.EntityId))
                coords[p.EntityId] = [p.CoordX, p.CoordY, p.CoordZ, p.CoordM];
        }
        if (!coords.ContainsKey(root) && TryCodepointCoord(root, out var floor))
            coords[root] = floor;
        if (members is not null && coords.ContainsKey(root)) members.Add(root);
        return root;
    }

    /// <summary>
    /// Tier-0 floor: a single-codepoint part collapses to its codepoint id, which the
    /// content builder never stages (seeded). Its coordinate comes from the perfcache.
    /// </summary>
    private static bool TryCodepointCoord(Hash128 id, out double[] coord)
    {
        coord = [];
        if (!CodepointPerfcache.IsLoaded || !CodepointPerfcache.TryLookupCodepoint(id, out _))
            return false;
        foreach (ref readonly var rec in CodepointPerfcache.Records)
        {
            if (rec.Hash == id)
            {
                coord = [rec.CoordX, rec.CoordY, rec.CoordZ, rec.CoordM];
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Ordered composition (the chess-position law): merkle id over member order, Content
    /// physicality whose trajectory is the member manifest, Karcher-mean coordinate.
    /// One member collapses to that member (tier-floor law); zero members compose nothing.
    /// </summary>
    private static Hash128? ComposeOrdered(
        SubstrateChangeBuilder b, List<Hash128> members, byte tier, Hash128 typeId,
        Hash128 sourceId, Dictionary<Hash128, double[]> coords, long observedAtUs)
    {
        if (members.Count == 0) return null;
        if (members.Count == 1) return members[0];

        Hash128 id = Hash128.Merkle(tier, System.Runtime.InteropServices.CollectionsMarshal
            .AsSpan(members));
        if (!coords.ContainsKey(id))
        {
            var flat = new double[members.Count * 4];
            for (int i = 0; i < members.Count; i++) coords[members[i]].CopyTo(flat, i * 4);
            coords[id] = Math4d.KarcherMean(flat);
        }
        double[] centroid = coords[id];

        b.AddEntity(id, tier, typeId, sourceId);
        Hash128 physId = PhysicalityId.Compute(id, PhysicalityType.Content);
        if (b.TrySeePhysicality(physId))
            b.AddPhysicalityPreSeen(new PhysicalityRow(
                Id: physId, EntityId: id, SourceId: sourceId,
                Type: PhysicalityType.Content,
                CoordX: centroid[0], CoordY: centroid[1],
                CoordZ: centroid[2], CoordM: centroid[3],
                HilbertIndex: Hilbert128.Encode(centroid),
                TrajectoryXyzm: Trajectory.Build(System.Runtime.InteropServices.CollectionsMarshal
                    .AsSpan(members)),
                NConstituents: members.Count,
                AlignmentResidual: null, SourceDim: null,
                ObservedAtUnixUs: observedAtUs));
        return id;
    }

    private static List<Hash128> Roots(params Hash128?[] roots)
    {
        var list = new List<Hash128>(roots.Length);
        foreach (var r in roots)
            if (r is { } id) list.Add(id);
        return list;
    }

    // ── governed identities and attestations ──────────────────────────────────────

    /// <summary>
    /// Canonical (non-content) identity with its name witnessed as content and linked
    /// via IS_INSTANCE_OF — the Tabular column/value law.
    /// </summary>
    private static Hash128 CanonicalEntity(
        SubstrateChangeBuilder b, string canonicalKey, string surfaceName, Hash128 typeId,
        Dictionary<Hash128, double[]>? coords)
    {
        Hash128 id = Hash128.OfCanonical(canonicalKey);
        b.AddEntity(id, EntityTier.Word, typeId, LaneSource);
        if (ContentEmitter.Emit(b, surfaceName, LaneSource) is { } nameRoot && nameRoot != id)
            b.AddAttestation(NativeAttestation.Categorical(
                id, Rel(AgentRelation.IsInstanceOf), nameRoot, LaneSource, null, TC.AppDerived));
        _ = coords;
        return id;
    }

    private static void AttestCanonical(
        SubstrateChangeBuilder b, long ts, Hash128 subject, string relation, Hash128 obj,
        Hash128 sessionId) =>
        Attest(b, ts, NativeAttestation.Categorical(
            subject, relation, obj, LaneSource, sessionId, TC.AppDerived));

    /// <summary>
    /// Metadata retention: every leftover provider field becomes (subject HAS_ATTRIBUTE
    /// root("key=value"))@ctx=session — queryable testimony, nothing dropped.
    /// </summary>
    private static void AttestMetaAttribute(
        SubstrateChangeBuilder b, long ts, Hash128 subject, Hash128 sessionId,
        string key, string value, Dictionary<Hash128, double[]> coords)
    {
        if (Witness(b, $"{key}={value}", LaneSource, coords, members: null) is { } kv)
            Attest(b, ts, NativeAttestation.Categorical(
                subject, Rel(AgentRelation.HasAttribute), kv, LaneSource, sessionId, TC.AppDerived));
    }

    private static void EmitUsage(
        SubstrateChangeBuilder b, long ts, Hash128 subject, Hash128 sessionId, AgentUsage usage,
        Dictionary<Hash128, double[]> coords)
    {
        AttestScalar(b, ts, subject, sessionId, Rel(AgentRelation.HasInputTokens), usage.InputTokens, coords);
        AttestScalar(b, ts, subject, sessionId, Rel(AgentRelation.HasOutputTokens), usage.OutputTokens, coords);
        AttestScalar(b, ts, subject, sessionId, Rel(AgentRelation.HasCacheReadTokens), usage.CacheReadTokens, coords);
        AttestScalar(b, ts, subject, sessionId, Rel(AgentRelation.HasCacheCreateTokens), usage.CacheCreateTokens, coords);
        if (usage.CostUsd is { } cost)
            AttestScalarText(b, ts, subject, sessionId, Rel(AgentRelation.HasCost),
                cost.ToString("0.######", CultureInfo.InvariantCulture), coords);
    }

    private static void AttestScalar(
        SubstrateChangeBuilder b, long ts, Hash128 subject, Hash128 sessionId,
        string relation, long? value, Dictionary<Hash128, double[]> coords)
    {
        if (value is { } v)
            AttestScalarText(b, ts, subject, sessionId, relation,
                v.ToString(CultureInfo.InvariantCulture), coords);
    }

    /// <summary>
    /// Scalar identity = the text-content law (ModelCoordinates.ScalarId): the entity for
    /// "4096" here IS the content root of "4096" everywhere in the substrate.
    /// </summary>
    private static void AttestScalarText(
        SubstrateChangeBuilder b, long ts, Hash128 subject, Hash128 sessionId,
        string relation, string value, Dictionary<Hash128, double[]> coords)
    {
        if (Witness(b, value, LaneSource, coords, members: null) is not { } scalarRoot)
            return;
        b.AddEntity(scalarRoot, EntityTier.Word, EntityTypeRegistry.Scalar, LaneSource);
        Attest(b, ts, NativeAttestation.Categorical(
            subject, relation, scalarRoot, LaneSource, sessionId, TC.AppDerived));
    }

    /// <summary>Event-time retention: the log's clock, never the ingest clock.</summary>
    private static void Attest(SubstrateChangeBuilder b, long eventUs, AttestationRow row) =>
        b.AddAttestation(eventUs > 0 ? row with { LastObservedAtUnixUs = eventUs } : row);

    private static bool IsRealModelId(string? model) =>
        model is { Length: > 0 } && model[0] != '<';

    /// <summary>Clamp arbitrary provider keys into the conversational identifier charset.</summary>
    internal static string SanitizeKey(string raw)
    {
        if (ConversationContent.IsValidIdentifier(raw)) return raw;
        var sb = new StringBuilder(Math.Min(raw.Length, 128));
        foreach (char c in raw)
        {
            if (sb.Length == 128) break;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '@' or '-' ? c : '_');
        }
        if (sb.Length == 0) sb.Append('_');
        return sb.ToString();
    }

    private sealed class UsageTotals
    {
        private long _in, _out, _cacheRead, _cacheCreate;
        private double _cost;
        private bool _anyTokens, _anyCost;

        public void Add(AgentUsage u)
        {
            if (u.InputTokens is { } i) { _in += i; _anyTokens = true; }
            if (u.OutputTokens is { } o) { _out += o; _anyTokens = true; }
            if (u.CacheReadTokens is { } r) { _cacheRead += r; _anyTokens = true; }
            if (u.CacheCreateTokens is { } c) { _cacheCreate += c; _anyTokens = true; }
            if (u.CostUsd is { } usd) { _cost += usd; _anyCost = true; }
        }

        public bool IsEmpty => !_anyTokens && !_anyCost;

        public AgentUsage ToUsage() => new(
            _anyTokens ? _in : null,
            _anyTokens ? _out : null,
            _anyTokens ? _cacheRead : null,
            _anyTokens ? _cacheCreate : null,
            _anyCost ? _cost : null);
    }
}
