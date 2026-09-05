using System.Reflection;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Identity-search law for GH #1398. Exact content-addressed resolution is the
/// terminal path; bounded fuzzy candidate expansion is reached only after an exact
/// miss. Response-shape tests alone cannot detect the expensive/contaminating fuzzy
/// query because both implementations may ultimately render the same exact row.
/// </summary>
public sealed class ChessPlayerExactLookupLawTests
{
    [Fact]
    public void ExactLookupTerminatesBeforeFuzzyCandidateExpansion()
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "app", "Laplace.Endpoints.OpenAICompat", "SubstrateClient.Chess.cs"));

        const string exactCall = "var exact = await ChessFindPlayerAsync(query, ct);";
        const string exactBranch = "if (exact is not null)";
        const string fuzzyCall = "ChessPlayerSearchCandidatesAsync(";

        int exact = source.IndexOf(exactCall, StringComparison.Ordinal);
        int terminal = source.IndexOf(exactBranch, exact < 0 ? 0 : exact, StringComparison.Ordinal);
        int fuzzy = source.IndexOf(fuzzyCall, terminal < 0 ? 0 : terminal, StringComparison.Ordinal);

        Assert.True(exact >= 0, "exact player lookup disappeared from the search path");
        Assert.True(terminal > exact, "exact lookup is not followed by a terminal hit branch");
        Assert.True(fuzzy > terminal,
            "fuzzy candidate expansion must occur only after the exact-hit terminal branch");
        Assert.DoesNotContain(".Concat(exact", source, StringComparison.Ordinal);

        // The old exact reader asked generic edges_raw to choose the best OUTCOME edge with
        // LIMIT 1. OUTCOME is shared, so that can disagree with the roster's canonical
        // (player, OUTCOME, Chess_Result) standing cell. Exact lookup must use the same
        // canonical candidate projection as the list, whose SQL exact CTE short-circuits
        // fuzzy expansion when the content-addressed player exists.
        Assert.DoesNotContain("NpgsqlSubstrateReads.ChessFindPlayerAsync(", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? stamped = typeof(ChessPlayerExactLookupLawTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static attribute => attribute.Key == "LaplaceRepoRoot")?.Value;
        if (stamped is not null && File.Exists(Path.Combine(stamped, "app", "Laplace.slnx")))
            return stamped;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "app", "Laplace.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repository root not found from test base directory");
    }
}
