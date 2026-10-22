using Laplace.Engine.Core;
using Laplace.Decomposers.Tests;
using Xunit;

namespace Laplace.Decomposers.Atomic2020.Tests;

public sealed class Atomic2020NoneTailTests
{
    [Fact]
    public async Task NativeTriplesRetainSourceValuesAndTheSourcesOwnRelationInventory()
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string fixture = Path.Combine(repository, "app", "Laplace.Decomposers.Tests", "Fixtures", "NativeRecipes",
            "atomic", "test.tsv");
        string[] claims = await NativeCorpusAssertions.ClaimsAsync("atomic-2020", fixture);
        Assert.Equal(new[]
        {
            "claim\t0\t1\tPersonX abandons ___\txNeed\tnone",
            "claim\t0\t1\tPersonX abandons ___\txNeed\tto grab the bag",
            "claim\t0\t1\tnone\txNeed\tto exist",
            "claim\t0\t1\tPersonX abandons ___\tNotDesires\tto stay",
        }, claims);
    }
}
