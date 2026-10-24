using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.CILI.Tests;

public sealed class CILIReferenceAdmissionTests
{
    [Fact]
    public async Task CanonicalNativeRecipeRetainsIdentifierSubjectsAndLanguageQualifiedDefinitions()
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string fixture = Path.Combine(Directory.GetParent(EtlManifest.RecipesRoot)!.FullName, "tests",
            "fixtures", "highway-identities", "cili", "ili.ttl");
        string[] claims = await NativeCorpusAssertions.ClaimsAsync("cili", fixture);
        Assert.Equal(2, claims.Length);
        Assert.Contains(claims, claim => claim.Contains("\ti46360\ta\tConcept", StringComparison.Ordinal));
        Assert.Contains(claims, claim => claim.Contains("\ti46360\tskos:definition\t[", StringComparison.Ordinal)
            && claim.EndsWith(", language:eng]", StringComparison.Ordinal));
        Assert.All(claims, claim => Assert.StartsWith("claim\t0\t1\ti46360\t", claim, StringComparison.Ordinal));
    }

    [Fact]
    public void SourceManifestPreservesCiliLicenseAndRelease()
    {
        Assert.Equal("CC-BY-4.0", CILISource.License.Spdx);
        Assert.Equal("2016 Initial release", CILISource.License.Version);
        Assert.Contains("Global Wordnet Association", CILISource.License.Copyright);
    }
}
