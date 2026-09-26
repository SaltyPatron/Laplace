using System.Reflection;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Identity-search law: exact content-addressed resolution terminates the search;
/// bounded fuzzy candidate expansion runs only after an exact miss. Pinned in source,
/// because a fuzzy expansion that still renders the exact row is invisible to
/// response-shape tests.
/// </summary>
public sealed class ChessPlayerExactLookupLawTests
{
    [Fact]
    public void ExactLookupTerminatesBeforeFuzzyCandidateExpansion()
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "app", "Laplace.Endpoints.OpenAICompat", "SubstrateClient.Chess.cs"));

        // One native operation (chess_roster.c) runs both paths; the managed client asks
        // it for exact-only resolution and does no candidate expansion of its own.
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

        // Exact lookup uses the roster's candidate projection, so a player's standing is
        // the canonical (player, OUTCOME, Chess_Result) cell, not whichever OUTCOME edge
        // a generic edge read would pick.
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
