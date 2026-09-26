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

        // Canonical ids are BLAKE3(name). Re-running the seed skips an identical name,
        // while a different name deriving an existing id reaches the unique-id constraint
        // and fails; a generic ON CONFLICT could not tell the two apart.
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
