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

        // One native operation owns both paths; the previous managed "exact"
        // call already expanded candidates on a miss, then searched them again.
        string native = File.ReadAllText(Path.Combine(root,
            "extension/laplace_substrate/src/chess_roster.c"));
        Assert.Contains("if (!SPI_processed && !exact_only)", native);
        Assert.Contains("chess.search_exact", native);
        Assert.Contains("chess.search_named_players", native);
        Assert.Contains("exactOnly: true", source);
        Assert.DoesNotContain("await ChessFindPlayerAsync(query, ct)", source);
        Assert.DoesNotContain("PlayerSearchScore", source);
        Assert.DoesNotContain("2000, ct", source);
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
