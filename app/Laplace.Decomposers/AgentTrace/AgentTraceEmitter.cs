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
///   session       → stable governed identity (tenant=provider, key=session id) whose
///                   Projection physicality is the ordered, growing turn manifest.
///                   The handle is deliberately not the content hash of that manifest.
///
/// Membership rides the per-tenant UserPrompt@/Response@ sources so replayed
/// logs fold onto the SAME consensus cells as live conversation; structure, usage
/// scalars and retained metadata ride the AgentTrace lane source. Every attestation
/// and composed physicality carries the LOG's event time, not ingest time.
/// </summary>
public static class AgentTraceEmitter
{
    private static readonly Hash128 LaneSource = AgentTraceSource.SourceId;

    private static string Rel(AgentRelation relation) => AgentRelations.Surface(relation);

    public readonly record struct ProviderScope(
        ConversationContent.TenantScope Tenant,
        Hash128 ToolSource)
    {
        public static ProviderScope Resolve(string provider) => new(
            ConversationContent.Resolve(provider),
            SubstrateCanonicalIds.Source($"ToolResult@{provider}"));
    }

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
        b.DeclareSourcePrior(LaneSource, TC.AppDerived)
            .DeclareSourcePrior(scope.Tenant.PromptSource, TC.UserPrompt)
            .DeclareSourcePrior(scope.Tenant.ResponseSource, TC.Response)
            // This prior describes the application-captured tool-result structure,
            // not a claim that the external tool output is true.
            .DeclareSourcePrior(scope.ToolSource, TC.AppDerived);
        Hash128 sessionId = ConversationContent.SessionId(
            session.Provider, SanitizeKey(session.SessionKey));
        long sessionUs = session.StartedAtUnixUs;
        int watermark = session.WitnessedTurnWatermark;

        b.AddEntity(sessionId, EntityTier.Document, EntityTypeRegistry.ConversationSession);

        var coords = new Dictionary<Hash128, double[]>();
        var turnIds = new List<Hash128>(session.Turns.Count);
        var turnCoords = new List<double[]>(session.Turns.Count);
        long turnsUsedUs = 0;
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
            Witness(b, turn.Text, roleSource, coords, members);
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

            bool witnessTurn = turnIds.Count > watermark;
            if (!witnessTurn)
            {
                if (turn.Usage is { IsEmpty: false } priorUsage) totals.Add(priorUsage);
                continue;
            }

            Attest(b, ts, NativeAttestation.Categorical(
                tid, Rel(AgentRelation.AppearsIn), sessionId, roleSource, sessionId,
                TC.AppDerived));

            // A role, a model id and a tool name are content: the text itself is the
            // entity, and the claim that uses it says what it is.
            if (ContentEmitter.Emit(b, turn.Role, LaneSource) is { } role)
                AttestCanonical(b, ts, tid, Rel(AgentRelation.HasRole), role, sessionId);
            if (IsRealModelId(turn.Model) && ContentEmitter.Emit(b, turn.Model!, LaneSource) is { } model)
                AttestCanonical(b, ts, tid, Rel(AgentRelation.AuthoredBy), model, sessionId);
            if (!string.IsNullOrEmpty(turn.StopReason))
                AttestProperty(b, ts, tid, sessionId, "stop_reason", turn.StopReason);

            if (turn.Usage is { IsEmpty: false } usage)
            {
                totals.Add(usage);
                EmitUsage(b, ts, tid, sessionId, usage, coords);
            }

            foreach (var (call, invocationId, inputRoot, resultRoot) in invocations)
            {
                if (ContentEmitter.Emit(b, call.Name, LaneSource) is not { } toolEntity) continue;
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
                    AttestProperty(b, ts, inv, sessionId, "is_error", "true");
            }

            foreach (var (k, v) in turn.Meta)
                AttestProperty(b, ts, tid, sessionId, k, v);
        }

        long endUs = session.EndedAtUnixUs > 0 ? session.EndedAtUnixUs : turnsUsedUs;

        // A session id is a stable governed handle. Its growing ordered turn manifest is a
        // projection of that handle, never the content identity of the handle itself.
        if (turnIds.Count > 0)
        {
            var flat = new double[turnIds.Count * 4];
            for (int i = 0; i < turnIds.Count; i++) turnCoords[i].CopyTo(flat, i * 4);
            double[] centroid = Math4d.KarcherMean(flat);
            Hash128 physId = PhysicalityId.Compute(sessionId, PhysicalityType.Projection);
            b.AddPhysicality(new PhysicalityRow(
                    Id: physId, EntityId: sessionId, SourceId: scope.Tenant.PromptSource,
                    Type: PhysicalityType.Projection,
                    CoordX: centroid[0], CoordY: centroid[1],
                    CoordZ: centroid[2], CoordM: centroid[3],
                    HilbertIndex: Hilbert128.Encode(centroid),
                    TrajectoryXyzm: Trajectory.Build(System.Runtime.InteropServices.CollectionsMarshal
                        .AsSpan(turnIds)),
                    NConstituents: turnIds.Count,
                    AlignmentResidual: null, SourceDim: null,
                    ObservedAtUnixUs: endUs));
        }

        if (turnIds.Count > 0)
        {
            Hash128 chain = sessionId;
            foreach (var tid in turnIds) chain = WatermarkChainStep(chain, tid);
            b.AddEntity(WatermarkId(sessionId, turnIds.Count, chain), EntityTier.Word,
                EntityTypeRegistry.AgentSessionWatermark);
        }

        if (turnIds.Count <= watermark) return;

        if (Witness(b, session.Title, scope.Tenant.PromptSource, coords, members: null) is { } title)
            Attest(b, endUs, NativeAttestation.Categorical(
                sessionId, Rel(AgentRelation.HasName), title, LaneSource, sessionId, TC.AppDerived));
        if (Witness(b, session.Cwd, scope.Tenant.PromptSource, coords, members: null) is { } cwd)
            Attest(b, endUs, NativeAttestation.Categorical(
                sessionId, Rel(AgentRelation.HasContext), cwd, LaneSource, sessionId, TC.AppDerived));
        if (session.GitBranch is { Length: > 0 } branch)
            AttestProperty(b, endUs, sessionId, sessionId, "gitBranch", branch);
        if (session.UserKey is { Length: > 0 } user
            && Witness(b, user, scope.Tenant.PromptSource, coords, members: null) is { } userRoot)
            Attest(b, endUs, NativeAttestation.Categorical(
                sessionId, Rel(AgentRelation.HasAttribution), userRoot, scope.Tenant.PromptSource, null,
                TC.UserPrompt));
        if (sessionUs > 0)
        {
            string date = DateTimeOffset.FromUnixTimeMilliseconds(sessionUs / 1000)
                .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (Witness(b, date, scope.Tenant.PromptSource, coords, members: null) is { } dateRoot)
                Attest(b, endUs, NativeAttestation.Categorical(
                    sessionId, Rel(AgentRelation.OnDate), dateRoot, LaneSource, sessionId, TC.AppDerived));
        }
        foreach (var (k, v) in session.Meta)
            AttestProperty(b, endUs, sessionId, sessionId, k, v);
        if (!totals.IsEmpty)
            EmitUsage(b, endUs, sessionId, sessionId, totals.ToUsage(), coords);
    }

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

        b.AddEntity(id, tier, typeId);
        Hash128 physId = PhysicalityId.Compute(id, PhysicalityType.Content);
        b.AddPhysicality(new PhysicalityRow(
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

    private static void AttestCanonical(
        SubstrateChangeBuilder b, long ts, Hash128 subject, string relation, Hash128 obj,
        Hash128 sessionId) =>
        Attest(b, ts, NativeAttestation.Categorical(
            subject, relation, obj, LaneSource, sessionId, TC.AppDerived));

    // A property the log states about a turn, invocation or session: the property value
    // [key, value] under HAS_ATTRIBUTE, never "key=value" text and never a relation per key.
    private static void AttestProperty(
        SubstrateChangeBuilder b, long ts, Hash128 subject, Hash128 sessionId, string key, string value)
    {
        if (ContentEmitter.StagePropertyValue(b, key, value, LaneSource) is { } fact)
            Attest(b, ts, NativeAttestation.Categorical(
                subject, Rel(AgentRelation.HasAttribute), fact, LaneSource, sessionId, TC.AppDerived));
    }

    // Token usage and cost under the provider API's own field names, so every agent's
    // usage converges on the same property values.
    private static void EmitUsage(
        SubstrateChangeBuilder b, long ts, Hash128 subject, Hash128 sessionId, AgentUsage usage,
        Dictionary<Hash128, double[]> coords)
    {
        _ = coords;
        AttestCount(b, ts, subject, sessionId, "input_tokens", usage.InputTokens);
        AttestCount(b, ts, subject, sessionId, "output_tokens", usage.OutputTokens);
        AttestCount(b, ts, subject, sessionId, "cache_read_input_tokens", usage.CacheReadTokens);
        AttestCount(b, ts, subject, sessionId, "cache_creation_input_tokens", usage.CacheCreateTokens);
        if (usage.CostUsd is { } cost)
            AttestProperty(b, ts, subject, sessionId, "cost_usd",
                cost.ToString("0.######", CultureInfo.InvariantCulture));
    }

    private static void AttestCount(
        SubstrateChangeBuilder b, long ts, Hash128 subject, Hash128 sessionId, string key, long? value)
    {
        if (value is { } v)
            AttestProperty(b, ts, subject, sessionId, key, v.ToString(CultureInfo.InvariantCulture));
    }

    private static void Attest(SubstrateChangeBuilder b, long eventUs, AttestationRow row) =>
        b.AddAttestation(eventUs > 0 ? row with { LastObservedAtUnixUs = eventUs } : row);

    private static bool IsRealModelId(string? model) =>
        model is { Length: > 0 } && model[0] != '<';

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
