using Xunit;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Abstractions.Tests;

public class DecomposerOptionsTests
{
    [Fact]
    public void Default_HasSafeDefaults()
    {
        var d = DecomposerOptions.Default;
        Assert.Equal(1, d.BatchSize);
        Assert.False(d.DryRun);
        Assert.True(d.ResumeFromCheckpoint);
        Assert.Null(d.CheckpointPath);
        Assert.Null(d.IncludeFilter);
        Assert.Null(d.ExcludeFilter);
    }

    [Fact]
    public void Record_EqualityByValue()
    {
        var a = DecomposerOptions.Default;
        var b = DecomposerOptions.Default with { };
        Assert.Equal(a, b);
        var c = a with { DryRun = true };
        Assert.NotEqual(a, c);
    }
}
