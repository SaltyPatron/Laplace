using System.Reflection;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class ConsensusInlineCompletionContractTests
{
    [Fact]
    public void BulkWriterOwnsBoundedFileCompletionPipeline()
    {
        var type = typeof(ConsensusAccumulatingWriter);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags publicInstance = BindingFlags.Instance | BindingFlags.Public;
        Assert.NotNull(type.GetField("_foldDepth", hidden));
        Assert.NotNull(type.GetField("_foldChainLock", hidden));
        Assert.NotNull(type.GetField("_outstanding", hidden));
        Assert.NotNull(type.GetField("_fileFolds", hidden));
        Assert.NotNull(type.GetMethod("EnqueueFoldAsync", hidden));
        Assert.NotNull(type.GetMethod("DrainFoldsAsync", publicInstance));
        Assert.NotNull(type.GetMethod("CompleteFileAsync", publicInstance));
    }
}
