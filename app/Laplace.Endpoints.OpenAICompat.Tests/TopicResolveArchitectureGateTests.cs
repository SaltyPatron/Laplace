using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Topic resolution is one logical read. It must not lease one connection, resolve
/// through a second datasource connection, then return to the first for labeling.
/// </summary>
public sealed class TopicResolveArchitectureGateTests
{
    [Fact]
    public void ResolveTopic_UsesOneCombinedResolveAndLabelOperation()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.Query.cs");
        var method = ExtractMethod(text, "public async Task<(byte[] Id, string Label)?> ResolveTopicAsync");

        Assert.Contains("NpgsqlSubstrateReads.ResolveRefWithLabelAsync(", method);
        Assert.DoesNotContain("OpenConnectionAsync", method);
        Assert.DoesNotContain("ResolveRefAsync(", method);
        Assert.DoesNotContain("LabelOrHexAsync(", method);
    }

    [Fact]
    public void CombinedResolveAndLabel_IsOneServerStatement()
    {
        var text = Read("app/Laplace.Substrate/Crud/Npgsql/NpgsqlSubstrateReads.Resolve.cs");
        Assert.Contains("SELECT converse.resolve_ref(@ref) AS id", text);
        Assert.Contains("realize.render_text_fast(r.id, 8)", text);
        Assert.Contains("converse.label_or_hex(r.id)", text);
        Assert.DoesNotContain("OpenConnectionAsync", text);
    }

    private static string ExtractMethod(string text, string signaturePrefix)
    {
        var start = text.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signaturePrefix}' not found");
        var nextMethod = text.IndexOf("\n    /// <summary>", start + signaturePrefix.Length, StringComparison.Ordinal);
        return nextMethod > start ? text[start..nextMethod] : text[start..];
    }

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
