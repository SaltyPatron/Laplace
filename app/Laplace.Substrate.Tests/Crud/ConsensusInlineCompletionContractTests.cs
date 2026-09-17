using System.Reflection;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class ConsensusInlineCompletionContractTests
{
    [Fact]
    public void WriterHasNoDeferredFoldQueueAuthority()
    {
        var type = typeof(ConsensusAccumulatingWriter);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        Assert.Null(type.GetField("_foldDepth", hidden));
        Assert.Null(type.GetField("_foldChainLock", hidden));
        Assert.Null(type.GetField("_outstanding", hidden));
        Assert.Null(type.GetMethod("EnqueueFoldAsync", hidden));
        Assert.Null(type.GetMethod("DrainFoldsAsync", hidden));
        Assert.Null(type.GetMethod("ObserveFoldFailureAsync", hidden));
    }
}
