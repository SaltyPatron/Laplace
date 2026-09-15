using System.Collections.Immutable;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.AgentTrace;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.AgentTrace.Tests;

public sealed class AgentTraceDecomposerTests
{
    static AgentTraceDecomposerTests()
    {
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    private const string SessionKey = "763989d6-3dd3-45f3-a5ef-9437faf5f921";

    private const string Fixture =
        """
        {"type":"user","uuid":"u1","parentUuid":null,"sessionId":"763989d6-3dd3-45f3-a5ef-9437faf5f921","timestamp":"2026-08-22T19:32:15.418Z","cwd":"/home/ahart/Projects/Laplace","gitBranch":"main","message":{"role":"user","content":"run the tests"}}
        {"type":"assistant","uuid":"a1","parentUuid":"u1","sessionId":"763989d6-3dd3-45f3-a5ef-9437faf5f921","timestamp":"2026-08-22T19:32:20.000Z","message":{"role":"assistant","model":"claude-opus-5","stop_reason":"tool_use","usage":{"input_tokens":120,"output_tokens":45,"cache_read_input_tokens":1000,"cache_creation_input_tokens":50},"content":[{"type":"thinking","thinking":"the gate is ctest then dotnet"},{"type":"text","text":"Running the gate now."},{"type":"tool_use","id":"tu1","name":"Bash","input":{"command":"just test"}}]}}
        {"type":"user","uuid":"u2","parentUuid":"a1","sessionId":"763989d6-3dd3-45f3-a5ef-9437faf5f921","timestamp":"2026-08-22T19:33:00.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu1","content":[{"type":"text","text":"all green"}],"is_error":false}]}}
        {"type":"assistant","uuid":"a2","parentUuid":"u2","sessionId":"763989d6-3dd3-45f3-a5ef-9437faf5f921","timestamp":"2026-08-22T19:33:05.000Z","message":{"role":"assistant","model":"claude-opus-5","stop_reason":"end_turn","usage":{"input_tokens":200,"output_tokens":12},"content":[{"type":"text","text":"Tests pass."}]}}
        """;

    private static async Task<(
        List<EntityRow> Entities,
        List<PhysicalityRow> Physicalities,
        List<AttestationRow> Attestations)> RunAsync(string dir)
    {
        var dec = new AgentTraceDecomposer();
        var ctx = new FakeContext(dir, new NullWriter());
        await dec.InitializeAsync(ctx);

        var entities = new List<EntityRow>();
        var physicalities = new List<PhysicalityRow>();
        var attestations = new List<AttestationRow>();
        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            entities.AddRange(change.Entities);
            physicalities.AddRange(change.Physicalities);
            attestations.AddRange(change.Attestations);
        }
        return (entities, physicalities, attestations);
    }

    [Fact]
    public async Task ClaudeLog_Emits_Session_Projection_Turn_Graph_And_Event_Times()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-agents-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".claude", "projects", "proj"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, ".claude", "projects", "proj", $"{SessionKey}.jsonl"),
                Fixture, new UTF8Encoding(false));

            var (entities, physicalities, attestations) = await RunAsync(dir);

            Hash128 sessionId = ConversationContent.SessionId("claude-code", SessionKey);
            Hash128 appearsIn = RelationTypeRegistry.Resolve("APPEARS_IN").Id;
            Hash128 hasRole = RelationTypeRegistry.Resolve("HAS_ROLE").Id;
            Hash128 authoredBy = RelationTypeRegistry.Resolve("AUTHORED_BY").Id;
            Hash128 calls = RelationTypeRegistry.Resolve("CALLS").Id;
            Hash128 hasInput = RelationTypeRegistry.Resolve("HAS_INPUT").Id;
            Hash128 hasResult = RelationTypeRegistry.Resolve("HAS_RESULT").Id;
            Hash128 hasInputTokens = RelationTypeRegistry.Resolve("HAS_INPUT_TOKENS").Id;
            Hash128 precedes = RelationTypeRegistry.Resolve("PRECEDES").Id;

            Assert.Contains(entities, e =>
                e.Id == sessionId && e.TypeId == EntityTypeRegistry.ConversationSession);
            var sessionPhys = Assert.Single(physicalities, p => p.EntityId == sessionId);
            Assert.Equal(PhysicalityType.Projection, sessionPhys.Type);
            Assert.Equal(
                PhysicalityId.Compute(sessionId, PhysicalityType.Projection), sessionPhys.Id);
            Assert.Equal(3, sessionPhys.NConstituents);
            Assert.NotNull(sessionPhys.TrajectoryXyzm);
            var orderedTurnIds = Trajectory.Constituents(sessionPhys.TrajectoryXyzm!);
            Assert.Equal(3, orderedTurnIds.Length);
            Assert.NotEqual(sessionId, Hash128.Merkle(EntityTier.Document, orderedTurnIds));

            var memberIds = attestations
                .Where(a => a.TypeId == appearsIn && a.ObjectId == sessionId
                            && a.ContextId == sessionId)
                .Select(a => a.SubjectId)
                .ToHashSet();
            foreach (var turnId in orderedTurnIds) Assert.Contains(turnId, memberIds);

            Hash128? promptRoot = ContentTierSpine.ResolveRoot("run the tests");
            Assert.NotNull(promptRoot);
            Assert.Equal(promptRoot!.Value, orderedTurnIds[0]);
            long promptUs =
                DateTimeOffset.Parse("2026-08-22T19:32:15.418Z").ToUnixTimeMilliseconds() * 1000;
            Assert.Contains(attestations, a =>
                a.TypeId == appearsIn && a.SubjectId == promptRoot.Value
                && a.ObjectId == sessionId && a.LastObservedAtUnixUs == promptUs);

            var turn2 = Assert.Single(physicalities, p => p.EntityId == orderedTurnIds[1]);
            Assert.Equal(PhysicalityType.Content, turn2.Type);
            Assert.True(turn2.NConstituents >= 3);
            var turn2Children = Trajectory.Constituents(turn2.TrajectoryXyzm!);
            Assert.Equal(orderedTurnIds[1], Hash128.Merkle(EntityTier.Document, turn2Children));
            Assert.Contains(entities, e =>
                e.Id == orderedTurnIds[1] && e.TypeId == EntityTypeRegistry.ConversationTurn);

            Assert.Contains(attestations, a =>
                a.TypeId == hasRole && a.SubjectId == orderedTurnIds[1]
                && a.ObjectId == Hash128.OfCanonical("agent/role/assistant/v1"));
            Assert.Contains(attestations, a =>
                a.TypeId == authoredBy && a.SubjectId == orderedTurnIds[1]
                && a.ObjectId == Hash128.OfCanonical("agent/model/claude-opus-5/v1"));
            Assert.Contains(attestations, a =>
                a.TypeId == calls && a.SubjectId == orderedTurnIds[1]
                && a.ObjectId == Hash128.OfCanonical("agent/tool/Bash/v1"));
            Assert.Contains(attestations, a => a.TypeId == hasInput);
            Hash128? resultRoot = ContentTierSpine.ResolveRoot("all green");
            Assert.NotNull(resultRoot);
            Assert.Contains(attestations, a =>
                a.TypeId == hasResult && a.ObjectId == resultRoot!.Value);
            Hash128? tokens120 = ContentTierSpine.ResolveRoot("120");
            Assert.NotNull(tokens120);
            Assert.Contains(attestations, a =>
                a.TypeId == hasInputTokens && a.SubjectId == orderedTurnIds[1]
                && a.ObjectId == tokens120!.Value);

            Hash128? replyRoot = ContentTierSpine.ResolveRoot("Running the gate now.");
            Assert.NotNull(replyRoot);
            Assert.DoesNotContain(attestations, a =>
                a.TypeId == precedes && a.SubjectId == promptRoot.Value
                && a.ObjectId == replyRoot!.Value);
            Assert.DoesNotContain(attestations, a =>
                a.TypeId == precedes && a.SubjectId == orderedTurnIds[1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Session_Totals_Aggregate_Turn_Usage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-agents-tot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".claude", "projects", "proj"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, ".claude", "projects", "proj", $"{SessionKey}.jsonl"),
                Fixture, new UTF8Encoding(false));
            var (_, _, attestations) = await RunAsync(dir);

            Hash128 sessionId = ConversationContent.SessionId("claude-code", SessionKey);
            Hash128 hasInputTokens = RelationTypeRegistry.Resolve("HAS_INPUT_TOKENS").Id;
            Hash128? total = ContentTierSpine.ResolveRoot("320");
            Assert.NotNull(total);
            Assert.Contains(attestations, a =>
                a.TypeId == hasInputTokens && a.SubjectId == sessionId
                && a.ObjectId == total!.Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
