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
        Assert.Equal(6, claims.Length);
        Assert.Contains(claims, claim => claim.Contains("\ti46360\ta\tConcept", StringComparison.Ordinal));
        Assert.Contains(claims, claim => claim.Contains("\ti46360\tskos:definition\t[", StringComparison.Ordinal)
            && claim.EndsWith(", language:eng]", StringComparison.Ordinal));
        Assert.Contains("claim\t0\t1\ti46360\tidentifier vocabulary\tili", claims);
        Assert.Contains("claim\t0\t1\t[ili, i46360]\tidentifies\ti46360", claims);
        Assert.Contains("claim\t0\t1\t[ili, 02084071-n]\tidentifies\ti46360", claims);
        Assert.Contains("claim\t0\t1\tlanguage:eng\tidentifier vocabulary\tlanguage", claims);
    }

    [Fact]
    public void SourceManifestPreservesCiliLicenseAndRelease()
    {
        Assert.Equal("CC-BY-4.0", CILISource.License.Spdx);
        Assert.Equal("2016 Initial release", CILISource.License.Version);
        Assert.Contains("Global Wordnet Association", CILISource.License.Copyright);
    }
}
