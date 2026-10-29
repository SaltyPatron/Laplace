using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class NativeRecipeConcurrencyTests
{
    [Fact]
    public async Task IndependentNativeCompilersFinishWhileTheManagedNativeGateIsOccupied()
    {
        using var fixture = new NativeCorpus();
        string path = Path.Combine(Directory.GetParent(fixture.Execution.Recipes)!.FullName,
            "tests", "fixtures", "highway-identities", "propbank", "abandon.xml");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task owner = Task.Factory.StartNew(() =>
        {
            lock (LaplaceCoreGate.Native) { entered.SetResult(); release.Wait(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var results = await Task.WhenAll(fixture.Claims("propbank", path, 1),
                fixture.Claims("propbank", path, 4)).WaitAsync(TimeSpan.FromSeconds(30));
            string[] first = results[0].Claims(), second = results[1].Claims();
            Assert.Contains(first, line => line.Split('\t')[3] == "abandon.01"
                && line.Split('\t')[4] == "sense description");
            Assert.Equal(first.Order(StringComparer.Ordinal), second.Order(StringComparer.Ordinal));
        }
        finally { release.Set(); await owner; }
    }

    [Fact]
    public async Task RejectedNativeInputCannotTerminateOrPoisonTheManagedCaller()
    {
        using var fixture = new NativeCorpus();
        string good = Path.Combine(Directory.GetParent(fixture.Execution.Recipes)!.FullName,
            "tests", "fixtures", "highway-identities", "propbank", "abandon.xml");
        string[] before = (await fixture.Claims("propbank", good)).Claims();
        string bad = fixture.Write("abandon.xml", "<frameset><predicate lemma=\"abandon\"><roleset");
        var rejected = await fixture.Claims("propbank", bad);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.NotEmpty(rejected.Error);
        string[] after = (await fixture.Claims("propbank", good)).Claims();
        Assert.Equal(before.Order(StringComparer.Ordinal), after.Order(StringComparer.Ordinal));
        Assert.NotEqual(default, Hash128.Text("managed caller remains usable"));
    }
}
