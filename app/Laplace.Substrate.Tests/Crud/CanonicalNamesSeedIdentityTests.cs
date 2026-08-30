using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.Ingestion.Tests;

public sealed class CanonicalNamesSeedIdentityTests
{
    [Fact]
    public void CanonicalSeed_IsIdempotentByName_ButDoesNotSilenceDerivedIdentityCollisions()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var seedPath = Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "seed",
            "canonical_names_seed.sql.in");
        var sql = File.ReadAllText(seedPath);

        // GH #959: canonical ids are BLAKE3(name). Re-running the manifest must
        // skip the exact same canonical NAME, while a different name deriving an
        // already-owned id must still reach PostgreSQL's unique-id constraint and
        // fail loudly. Generic ON CONFLICT cannot distinguish those two cases.
        Assert.DoesNotContain("ON CONFLICT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE existing.name = v.name", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE existing.id =", sql, StringComparison.OrdinalIgnoreCase);

        var names = Regex.Matches(sql, @"\('(?<name>[^']+)'\)")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        Assert.NotEmpty(names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }
}
