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
        Assert.Contains("ConversationContent.TryBuildTurnChange", text);
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
        Assert.Contains("ConversationContent.TryBuildTurnChange", depositTurn);
        Assert.DoesNotContain("UserPromptContent.BuildBootstrapChange", depositTurn);
    }

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
