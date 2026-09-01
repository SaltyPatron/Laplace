using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// `agents` source: batch ingest of AI-agent session logs from EVERY provider format the
/// adapter registry knows (Claude Code, Codex, Gemini/Qwen, Antigravity, Copilot,
/// Cursor, generic role-shaped JSON). Witness-unit lane: an explicit path (file or
/// directory) is the boundary; with no path it discovers the current user's provider
/// roots under $HOME. One session = one record; the shared multi-file spine owns
/// parallelism, working sets, batching, and per-file resume (a re-run true-skips
/// unchanged log files by content identity).
/// </summary>
public sealed class AgentTraceDecomposer
    : ComposeDecomposerMultiFile<AgentSession, AgentTraceSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = AgentTraceSource.SourceId;

    private readonly ConcurrentDictionary<string, IAgentTraceAdapter> _adapterByLabel =
        new(StringComparer.Ordinal);
    private readonly ConcurrentStringSet _canonicalNames = new(StringComparer.Ordinal);

    /// <summary>Per-process guard: each provider's tenant sources bootstrap once.</summary>
    private static readonly ConcurrentDictionary<string, byte> BootstrappedProviders =
        new(StringComparer.Ordinal);

    public override int LayerOrder => 2;
    protected override double SourceTrust => Abstractions.SourceTrust.AppDerived;
    protected override string BatchLabelPrefix => "agents";

    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    protected override async Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        // Tenant witness identities (spec 34): UserPrompt@/Response@/ToolResult@ per
        // provider whose files the run will touch, registered before any turn composes.
        foreach (var provider in DiscoverProviders(context.EcosystemPath))
        {
            if (!BootstrappedProviders.TryAdd(provider, 0)) continue;
            var scope = AgentTraceEmitter.ProviderScope.Resolve(provider);
            var toolBoot = new BootstrapIntentBuilder(
                scope.ToolSource, $"ToolResult@{provider}",
                SubstrateCanonicalIds.TrustClass("ToolResultContent"));
            foreach (var r in SourceVocabularyBootstrap.ExpandRelationsWithFamily(
                         [AgentRelations.Surface(AgentRelation.AppearsIn)]))
                toolBoot.AddRelationType(r);
            var changes = ConversationContent.BuildTenantBootstrapChanges(scope.Tenant).ToList();
            changes.Add(toolBoot.Build());
            await context.Writer.ApplyManyAsync(changes, ct);

            _canonicalNames.Add(SubstrateCanonicalKeys.Source(scope.Tenant.PromptSourceName));
            _canonicalNames.Add(SubstrateCanonicalKeys.Source(scope.Tenant.ResponseSourceName));
            _canonicalNames.Add(SubstrateCanonicalKeys.Source($"ToolResult@{provider}"));
        }
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        _adapterByLabel.Clear();
        var result = new List<(string Path, string Label)>();
        int i = 0;
        foreach (var (path, adapter) in EnumerateSessionFiles(ecosystemPath))
        {
            string label = $"agents/{adapter.ProviderKey}/{i++}/{Path.GetFileName(path)}";
            _adapterByLabel[label] = adapter;
            result.Add((path, label));
        }
        return result;
    }

    protected override async IAsyncEnumerable<AgentSession> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var adapter = _adapterByLabel.TryGetValue(fileLabel, out var a)
            ? a
            : AgentTraceAdapters.Resolve(filePath);
        if (adapter is null) yield break;
        await foreach (var session in adapter.ParseAsync(filePath, ct))
            yield return await ResolveWatermarkAsync(session, ct);
    }

    /// <summary>
    /// Grown-log protection: probe the session's Agent_Session_Watermark prefixes in ONE
    /// batched existence bitmap and mark the deepest witnessed prefix, so Compose skips
    /// re-witnessing testimony the substrate already holds. A probe miss only ever
    /// re-witnesses (safe); a hit is only possible for a byte-identical turn prefix.
    /// </summary>
    private async ValueTask<AgentSession> ResolveWatermarkAsync(AgentSession session, CancellationToken ct)
    {
        var reader = ContainmentReader;
        if (reader is null || !CodepointPerfcache.IsLoaded) return session;
        var turnIds = AgentTraceEmitter.ComputeComposedTurnIds(session);
        if (turnIds.Count == 0) return session;

        Hash128 sessionId = ConversationContent.SessionId(
            session.Provider, AgentTraceEmitter.SanitizeKey(session.SessionKey));
        var candidates = AgentTraceEmitter.WatermarkCandidates(sessionId, turnIds);
        byte[] bitmap = await reader.EntitiesExistBitmapAsync(candidates, ct);
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            if (BitmapBits.IsSet(bitmap, i))
                return session with { WitnessedTurnWatermark = i + 1 };
        }
        return session;
    }

    protected override void Compose(AgentSession session, SubstrateChangeBuilder b) =>
        AgentTraceEmitter.Emit(b, session);

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateSessionFiles(context.EcosystemPath).Select(f => f.Path).ToList();
        return Task.FromResult(IngestInventory.FromFiles(
            "sessions", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
    }

    public override async Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default) =>
        (await DescribeInputAsync(context, DecomposerOptions.Default, ct))?.TotalInputUnits;

    // ── discovery ─────────────────────────────────────────────────────────────────

    private static IEnumerable<(string Path, IAgentTraceAdapter Adapter)> EnumerateSessionFiles(
        string ecosystemPath)
    {
        if (string.IsNullOrWhiteSpace(ecosystemPath) || ecosystemPath == "auto")
        {
            // Path-less run: this user's provider roots. Only format-specific adapters
            // claim files here — the generic fallback needs an explicit path.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var adapter in AgentTraceAdapters.All)
            {
                if (adapter is GenericJsonAdapter) continue;
                foreach (var root in adapter.DefaultRoots(home))
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in EnumerateOrdered(root))
                        if (adapter.CanHandle(f))
                            yield return (f, adapter);
                }
            }
            yield break;
        }

        if (IngestInput.IsSingleFile(ecosystemPath))
        {
            if (AgentTraceAdapters.Resolve(ecosystemPath) is { } single)
                yield return (ecosystemPath, single);
            yield break;
        }

        if (!Directory.Exists(ecosystemPath)) yield break;
        foreach (var f in EnumerateOrdered(ecosystemPath))
            if (AgentTraceAdapters.Resolve(f) is { } adapter)
                yield return (f, adapter);
    }

    private static IEnumerable<string> EnumerateOrdered(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);

    private static IEnumerable<string> DiscoverProviders(string ecosystemPath) =>
        EnumerateSessionFiles(ecosystemPath)
            .Select(f => f.Adapter.ProviderKey)
            .Distinct(StringComparer.Ordinal);
}
