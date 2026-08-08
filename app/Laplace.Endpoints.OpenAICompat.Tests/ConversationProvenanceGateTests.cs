using System.Runtime.CompilerServices;
using System.Linq;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Spec 34 architecture gate — red until the conversational surface is real, and
/// red again the moment any of its known regressions (the conventional-chatbot
/// reflexes) creep back:
///   - hand-rolled session ids (SHA256/DeriveSessionId),
///   - canned assistant prose,
///   - substring model routing,
///   - turns without tenant/session provenance,
///   - turn deposits that emit no testimony.
/// Text pins over the source tree, DecomposerArchitectureGateTests style: cheap,
/// always-on, no DB.
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
        Assert.Contains("ModelCatalog.IsConverse", text);
    }

    [Fact]
    public void TurnWitness_CarriesTenantAndSessionProvenance()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/TurnWitness.cs");
        Assert.Contains("record struct TurnItem", text);
        Assert.Contains("string Tenant", text);
        Assert.Contains("Hash128 SessionId", text);
        // The close sequence moved to the shared TurnCloser; what this lane must
        // still prove is that it routes a turn through it WITH tenant and session.
        Assert.Contains("new TurnCloser(", text);
        Assert.Contains("closer.CloseAsync(", text);
        Assert.Contains("item.Tenant, item.SessionId", text);
    }

    /// <summary>
    /// ONE CLOSE, THREE LANES. The close sequence — floor gate, accumulating writer,
    /// tenant scope, bootstrap-once, attribute-once, build, apply — is TurnCloser's
    /// and no frontend may re-derive it. It was previously copied into MCP and the
    /// HTTP lane (which had already diverged: only HTTP checked the substrate floor)
    /// while the CLI ran a weaker fourth path with no tenant or session at all.
    /// </summary>
    [Fact]
    public void TurnCloser_IsTheOnlyCloseSequence()
    {
        var closer = Read("app/Laplace.Substrate/Ingestion/TurnCloser.cs");
        Assert.Contains("ConversationContent.Resolve", closer);
        Assert.Contains("ConversationContent.BuildTenantBootstrapChanges", closer);
        Assert.Contains("ConversationContent.TryBuildTurnChange", closer);
        Assert.Contains("ConsensusAccumulatingWriter", closer);   // fold inline, not deferred
        Assert.Contains("FloorPresentAsync", closer);             // the gate all lanes now share

        // No frontend re-derives it.
        foreach (var lane in new[]
                 {
                     "app/Laplace.Endpoints.Mcp/SubstrateTools.cs",
                     "app/Laplace.Endpoints.OpenAICompat/TurnWitness.cs",
                     "app/Laplace.Cli/QueryCommands.cs",
                 })
        {
            var text = Read(lane);
            Assert.DoesNotContain("ConversationContent.BuildTenantBootstrapChanges", text);
            Assert.DoesNotContain("ConversationContent.TryBuildTurnChange", text);
        }
    }

    /// <summary>
    /// converse.chat() is the ONLY conversational entry point (CLAUDE.md / spec 36): the CLI
    /// used to call generation.walk_text() directly with four hardcoded knobs, making it
    /// a sibling entry point that skipped language inference, the specificity
    /// election, shape dispatch and the responder family.
    /// </summary>
    [Fact]
    public void CliChat_GoesThroughChatAndClosesWithProvenance()
    {
        var text = Read("app/Laplace.Cli/QueryCommands.cs");
        // CODE only: the method's comment block names walk_text to record what it
        // replaced, and a gate that cannot tell prose from a call site is a gate
        // that punishes documenting the fix.
        var chat = StripComments(ExtractMethod(text, "public static async Task<int> ChatAsync"));
        // Chat SQL lives in NpgsqlSubstrateReads.ChatAsync; CLI must call that, not walk_text.
        Assert.Contains("ChatAsync(", chat);
        Assert.DoesNotContain("generation.walk_text(", chat);
        Assert.Contains("closer.CloseAsync(", chat);
        // A CLI turn carries a real session, minted through the canonical id law.
        Assert.Contains("ConversationContent.SessionId(", text);
    }

    [Fact]
    public void ConversationContent_EmitsTurnLevelTestimony()
    {
        var text = Read("app/Laplace.Substrate/Abstractions/ConversationContent.cs");
        Assert.Contains("\"APPEARS_IN\"", text);
        Assert.Contains("\"PRECEDES\"", text);
        Assert.Contains("\"HAS_ATTRIBUTION\"", text);
        // Ids mint through the canonical system, never a hand hash.
        Assert.Contains("SubstrateCanonicalIds", text);
        Assert.DoesNotContain("SHA256", text);
    }

    [Fact]
    public void McpDepositTurn_UsesConversationProvenance()
    {
        // Scoped to the DepositTurn method itself: the MCP surface also has a
        // standalone `witness` tool (a note with no session to scope) that
        // legitimately deposits through the plain UserPrompt/Response sources —
        // only a conversational turn's deposit must carry tenant/session provenance.
        var text = Read("app/Laplace.Endpoints.Mcp/SubstrateTools.cs");
        var depositTurn = ExtractMethod(text, "private void DepositTurn");
        // Routes through the shared closer WITH the tenant — the provenance the
        // plain note lane deliberately lacks.
        Assert.Contains("CloseAsync(McpTenant", depositTurn);
        Assert.DoesNotContain("UserPromptContent.BuildBootstrapChange", depositTurn);
        // The note lane still exists and still uses the plain sources, on its own
        // writer and its own latch — a failure there must not disable turns.
        Assert.Contains("UserPromptContent.BuildBootstrapChange", text);
        Assert.Contains("_plainWriterBroken", text);
    }

    /// <summary>
    /// The HTTP converse lane rides converse.chat() — the one conversational entry point —
    /// with recall_session only as the truthful-absence fallback (PR #892). Before
    /// this pin the lane read recall_session directly and answered "What is a dog?"
    /// with a phrase-lookup miss on the deployed box. Also pins the provenance rule:
    /// a chat reply's eff_mu/witnesses are ABSENT, never fabricated as zero.
    /// </summary>
    [Fact]
    public void HttpConverse_GoesThroughChatBeforeRecallFallback()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.cs");
        var method = StripComments(ExtractMethod(text,
            "private static async Task<IReadOnlyList<ConverseRow>> RecallSessionAsync"));
        var chatIdx = method.IndexOf("NpgsqlSubstrateReads.ChatAsync(", StringComparison.Ordinal);
        var recallIdx = method.IndexOf("NpgsqlSubstrateReads.RecallSessionAsync(", StringComparison.Ordinal);
        Assert.True(chatIdx >= 0, "converse lane must consult NpgsqlSubstrateReads.ChatAsync");
        Assert.True(recallIdx > chatIdx, "recall_session is the fallback, consulted after converse.chat()");
        Assert.Contains("ConverseRow(reply, null, null)", method);
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

    [Fact]
    public void Spec34_Exists()
    {
        Assert.True(File.Exists(RepoPath("docs/specs/34_Conversational_Provenance.md")),
            "spec 34 (conversational provenance) is binding and must exist");
    }

    private static IEnumerable<string> EndpointSources() =>
        Directory.EnumerateFiles(
            RepoPath("app/Laplace.Endpoints.OpenAICompat"), "EndpointMappings.*.cs",
            SearchOption.TopDirectoryOnly);

    private static string Read(string repoRelative) => File.ReadAllText(RepoPath(repoRelative));

    private static string RepoPath(string repoRelative) =>
        Path.Combine(RepoRoot(), repoRelative.Replace('/', Path.DirectorySeparatorChar));

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null &&
               !(Directory.Exists(Path.Combine(dir.FullName, "docs"))
                 && Directory.Exists(Path.Combine(dir.FullName, "app"))))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root not found above test source");
    }
}
