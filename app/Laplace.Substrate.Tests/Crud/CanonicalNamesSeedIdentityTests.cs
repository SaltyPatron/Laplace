using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.Ingestion.Tests;

public sealed class CanonicalNamesSeedIdentityTests
{
    [Fact]
    public void CanonicalSeed_DoesNotSilenceDerivedIdentityCollisions()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var seedPath = Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "seed",
            "canonical_names_seed.sql.in");
        var sql = File.ReadAllText(seedPath);

        // GH #959: canonical ids are BLAKE3(name). A collision in this greenfield
        // manifest is an identity-law failure, not idempotency. Let PostgreSQL's
        // unique id constraint reject it instead of discarding one name silently.
        Assert.DoesNotContain("DO NOTHING", sql, StringComparison.OrdinalIgnoreCase);

        var names = Regex.Matches(sql, @"\('(?<name>[^']+)'\)")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        Assert.NotEmpty(names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }
}
