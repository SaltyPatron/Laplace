using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.AgentTrace;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.AgentTrace.Tests;

/// <summary>
/// Grown-log re-witness protection: the watermark entity is content-addressed over the
/// composed-turn prefix, the resolve-path ids match the witness-path ids (the drift
/// guard), and a re-ingest with a deeper log witnesses ONLY the delta.
/// </summary>
public sealed class AgentTraceWatermarkTests
{
    static AgentTraceWatermarkTests()
    {
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    private const string SessionKey = "5c0ffee0-0000-4000-8000-000000000001";

    private static string Line(string type, string uuid, long minute, string messageJson) =>
        $"{{\"type\":\"{type}\",\"uuid\":\"{uuid}\",\"parentUuid\":null,"
        + $"\"sessionId\":\"{SessionKey}\","
        + $"\"timestamp\":\"2026-08-22T19:{minute:00}:00.000Z\",\"message\":{messageJson}}}";

    private static readonly string[] BaseLines =
    [
        Line("user", "u1", 30, """{"role":"user","content":"please check the fold"}"""),
        Line("assistant", "a1", 31,
            """{"role":"assistant","model":"claude-opus-5","stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":5},"content":[{"type":"text","text":"the fold looks healthy"}]}"""),
    ];

    private static readonly string GrowthLine =
        Line("user", "u2", 32, """{"role":"user","content":"now check the journals"}""");

    /// <summary>Reader whose presence answers come from a prior run's captured entity ids.</summary>
    private sealed class SeededReader(HashSet<Hash128> present) : ISubstrateReader
    {
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            var bm = new byte[BitmapBits.ByteLength(candidates.Count)];
            for (int i = 0; i < candidates.Count; i++)
                if (present.Contains(candidates[i]))
                    bm[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bm);
        }
    }

    private static async Task<(
        List<EntityRow> Entities, List<AttestationRow> Attestations)> RunAsync(
        string dir, ISubstrateReader reader)
    {
        var dec = new AgentTraceDecomposer();
        var ctx = new FakeContext(dir, new NullWriter()) { Reader = reader };
        await dec.InitializeAsync(ctx);
        var entities = new List<EntityRow>();
        var attestations = new List<AttestationRow>();
        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            entities.AddRange(change.Entities);
            attestations.AddRange(change.Attestations);
        }
        return (entities, attestations);
    }

    private static async Task<string> WriteFixtureAsync(string dir, IEnumerable<string> lines)
    {
        string proj = Path.Combine(dir, ".claude", "projects", "proj");
        Directory.CreateDirectory(proj);
        string path = Path.Combine(proj, $"{SessionKey}.jsonl");
        await File.WriteAllTextAsync(path, string.Join('\n', lines), new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task Witness_Path_Deposits_The_Resolve_Path_Watermark()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-wm-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WriteFixtureAsync(dir, BaseLines);
            var (entities, _) = await RunAsync(dir, new NullReader());

            // Recompute the candidate ids the PROBE would use; the deposited watermark
            // must be exactly the deepest one, or grown-log resume can never hit.
            var session = await ParseSessionAsync(dir);
            var turnIds = AgentTraceEmitter.ComputeComposedTurnIds(session);
            Assert.Equal(2, turnIds.Count);
            Hash128 sessionId = ConversationContent.SessionId("claude-code", SessionKey);
            var candidates = AgentTraceEmitter.WatermarkCandidates(sessionId, turnIds);

            Assert.Contains(entities, e =>
                e.Id == candidates[^1] && e.TypeId == EntityTypeRegistry.AgentSessionWatermark);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Grown_Log_ReIngest_Witnesses_Only_The_Delta()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-wm-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WriteFixtureAsync(dir, BaseLines);
            var first = await RunAsync(dir, new NullReader());

            // The log grows by one turn; the prior run's rows are "in the substrate".
            var present = first.Entities.Select(e => e.Id).ToHashSet();
            await WriteFixtureAsync(dir, BaseLines.Append(GrowthLine));
            var second = await RunAsync(dir, new SeededReader(present));

            Hash128 sessionId = ConversationContent.SessionId("claude-code", SessionKey);
            Hash128 appearsIn = RelationTypeRegistry.Resolve(
                AgentRelations.Surface(AgentRelation.AppearsIn)).Id;
            Hash128? oldPrompt = ContentTierSpine.ResolveRoot("please check the fold");
            Hash128? oldReply = ContentTierSpine.ResolveRoot("the fold looks healthy");
            Hash128? newPrompt = ContentTierSpine.ResolveRoot("now check the journals");
            Assert.NotNull(oldPrompt);
            Assert.NotNull(newPrompt);

            var membership = second.Attestations
                .Where(a => a.TypeId == appearsIn && a.ObjectId == sessionId)
                .Select(a => a.SubjectId)
                .ToHashSet();
            // Only the NEW turn re-witnesses membership; the prefix is watermark-skipped.
            Assert.Contains(newPrompt!.Value, membership);
            Assert.DoesNotContain(oldPrompt!.Value, membership);
            Assert.DoesNotContain(oldReply!.Value, membership);

            // And the deeper watermark (3 turns) is deposited for the next growth.
            var session = await ParseSessionAsync(dir);
            var turnIds = AgentTraceEmitter.ComputeComposedTurnIds(session);
            Assert.Equal(3, turnIds.Count);
            var candidates = AgentTraceEmitter.WatermarkCandidates(sessionId, turnIds);
            Assert.Contains(second.Entities, e => e.Id == candidates[^1]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Unchanged_Log_ReIngest_Emits_No_New_Testimony()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-wm-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WriteFixtureAsync(dir, BaseLines);
            var first = await RunAsync(dir, new NullReader());
            var present = first.Entities.Select(e => e.Id).ToHashSet();

            var second = await RunAsync(dir, new SeededReader(present));
            // The spine still deposits per-file completion markers (plumbing); the LANE
            // owes zero testimony — nothing carries the session as context.
            Hash128 sessionId = ConversationContent.SessionId("claude-code", SessionKey);
            Assert.DoesNotContain(second.Attestations, a => a.ContextId == sessionId);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static async Task<AgentSession> ParseSessionAsync(string dir)
    {
        string path = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories).Single();
        await foreach (var s in new ClaudeCodeAdapter().ParseAsync(path, CancellationToken.None))
            return s;
        throw new InvalidOperationException("fixture parsed no session");
    }
}
