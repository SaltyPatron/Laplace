using System.Runtime.CompilerServices;
using System.Linq;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Source-text pins for the conversation surfaces (Wiki Sequence 21. Sessions): every surface
/// that converses, the OpenAI-compatible endpoints, MCP `chat` and the CLI, runs the one forward
/// program through <c>ConversationTurns</c>, and the Engine (<c>laplace turn</c>) answers and
/// witnesses the turn. No surface composes a turn, writes a row or reads a second substrate's
/// chat. Reads the source tree only; no database.
/// </summary>
public sealed class ConversationProvenanceGateTests
{
    [Fact]
    public void InferenceEndpoints_NoHandRolledSessionIds()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/EndpointMappings.Inference.cs");
        Assert.DoesNotContain("SHA256", text);
        Assert.DoesNotContain("DeriveSessionId", text);
    }

    [Fact]
    public void InferenceEndpoints_NoCannedAssistantProse()
    {
        foreach (var file in EndpointSources())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("I hold no consensus", text);
        }
    }

    [Fact]
    public void InferenceEndpoints_NoSubstringModelRouting()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/EndpointMappings.Inference.cs");
        Assert.DoesNotContain("Contains(\"converse\"", text);
        Assert.DoesNotContain("Contains(\"form\"", text);
        Assert.Contains("ModelCatalog.IsChatModel", text);
        Assert.Contains("ModelCatalog.IsCompletionsModel", text);
    }

    /// <summary>
    /// ONE PROGRAM, EVERY SURFACE. Each conversation surface hands its turn to the Engine
    /// through <c>ConversationTurns</c>; none reads the retired chat functions or builds a turn.
    /// </summary>
    [Theory]
    [InlineData("app/Laplace.Endpoints.OpenAICompat/EndpointMappings.Inference.cs")]
    [InlineData("app/Laplace.Endpoints.Mcp/SubstrateTools.cs")]
    [InlineData("app/Laplace.Cli/QueryCommands.cs")]
    public void ConversationSurfaces_RunTheEngineTurn(string surface)
    {
        var text = StripComments(Read(surface));
        Assert.Contains("TurnAsync(", text);
        foreach (var retired in new[]
                 {
                     "NpgsqlSubstrateReads.ChatAsync(", "ForwardTurnAsync(", "ForwardTurnStepsAsync(",
                     "ForwardTurnObservedStepsAsync(", "TurnCloser", "TryBuildTurnChange",
                     "BuildTenantBootstrapChanges", "ApplyConversationTurnAsync",
                 })
            Assert.DoesNotContain(retired, text);
    }

    /// <summary>
    /// The turn is the Engine's: <c>laplace turn</c> (or <c>said</c>) with its receipt. The
    /// orchestrator composes no identity, opens no database connection and writes no row.
    /// </summary>
    [Fact]
    public void ConversationTurns_RunsTheEngineAndComposesNothing()
    {
        var turns = StripComments(Read("app/Laplace.Substrate/Ingestion/ConversationTurns.cs"));
        var engine = StripComments(Read("app/Laplace.Substrate/Abstractions/NativeRecipeExecution.Turns.cs"));
        Assert.Contains(".TurnAsync(", turns);
        Assert.Contains("\"turn\"", engine);
        Assert.Contains("\"said\"", engine);
        Assert.Contains("\"--receipt\"", engine);
        Assert.Contains("\"--files\"", engine);
        foreach (var text in new[] { turns, engine })
            foreach (var forbidden in new[]
                     {
                         "ContentTierSpine", "HashComposer", "SubstrateChangeBuilder", "ConsensusAccumulatingWriter",
                         "NpgsqlCatalog", "OpenConnectionAsync", "Blake3",
                     })
                Assert.DoesNotContain(forbidden, text);
    }

    /// <summary>The catalog keeps no statement of the retired turn writer or its chat reads.</summary>
    [Fact]
    public void SqlCatalog_HoldsNoSecondConversationSubstrate()
    {
        var catalog = Read("engine/core/src/sql_catalog.def");
        foreach (var retired in new[]
                 {
                     "\"conversation.append_turns\"", "\"conversation.forward_turn\"",
                     "\"conversation.forward_turn_observed\"", "\"converse.chat\"",
                     "session_append_turns",
                 })
            Assert.DoesNotContain(retired, catalog);
    }

    [Fact]
    public void McpChatTurn_IsTheEngineTurnOfTheMcpParticipant()
    {
        var text = Read("app/Laplace.Endpoints.Mcp/SubstrateTools.cs");
        var chat = StripComments(ExtractMethod(text, "private (string, bool) ChatTurn"));
        Assert.Contains("_turns.Value.TurnAsync(McpTenant", chat);
        // Notes and feedback are what the runtime witness mcp-local states, recorded by the Engine.
        Assert.Contains("_witnesses.Value.RecordAsync(new NativeRuntimeWitness(McpTenant", text);
        Assert.Contains("FeedbackStatements.StateAsync(_witnesses.Value, _db, McpTenant", text);
        Assert.DoesNotContain("ConsensusAccumulatingWriter", text);
        Assert.DoesNotContain("FeedbackContent", text);
    }

    /// <summary>Drops // line comments so a pin tests the call sites, not the prose.</summary>
    private static string StripComments(string source) =>
        string.Join('\n', source.Split('\n')
            .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));

    private static string ExtractMethod(string text, string signaturePrefix)
    {
        var start = text.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signaturePrefix}' not found");
        var nextMethod = text.IndexOf("\n    private ", start + signaturePrefix.Length, StringComparison.Ordinal);
        var nextPublic = text.IndexOf("\n    public ", start + signaturePrefix.Length, StringComparison.Ordinal);
        var candidates = new[] { nextMethod, nextPublic }.Where(i => i > 0);
        var end = candidates.Any() ? candidates.Min() : text.Length;
        return text[start..end];
    }

    private static IEnumerable<string> EndpointSources() =>
        Directory.EnumerateFiles(
            RepoPath("app/Laplace.Endpoints.OpenAICompat"), "EndpointMappings.*.cs",
            SearchOption.TopDirectoryOnly);

    private static string Read(string repoRelative)
    {
        var path = RepoPath(repoRelative);
        var text = File.ReadAllText(path);
        var parent = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var dir = Path.Combine(parent, stem);
        if (Path.GetExtension(path) == ".def" && Directory.Exists(dir))
            text += "\n" + string.Join("\n", Directory.GetFiles(dir, "*.def").OrderBy(f => f).Select(File.ReadAllText));
        if (Path.GetExtension(path) == ".cs" && !stem.Contains('.'))
            text += "\n" + string.Join("\n", Directory.GetFiles(parent, $"{stem}.*.cs").OrderBy(f => f).Select(File.ReadAllText));
        return text;
    }

    private static string RepoPath(string repoRelative) =>
        Path.Combine(RepoRoot(), repoRelative.Replace('/', Path.DirectorySeparatorChar));

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null &&
               !(Directory.Exists(Path.Combine(dir.FullName, "engine"))
                 && Directory.Exists(Path.Combine(dir.FullName, "app"))))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root not found above test source");
    }
}
