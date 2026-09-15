using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests.Core;

[Trait("Tier", "fast")]
public sealed class SyzygyPathTests
{
    [Fact]
    public void PathListNormalizesEachDirectoryWithoutMergingComponents()
    {
        string first = Path.Combine(Path.GetTempPath(), "syzygy", "3-4-5");
        string second = Path.Combine(Path.GetTempPath(), "syzygy", "6", "DTZ");
        string input = $" {first}{Path.DirectorySeparatorChar}{Path.PathSeparator}{second} "
            + $"{Path.PathSeparator}{first}{Path.PathSeparator}";
        Assert.Equal(string.Join(Path.PathSeparator, first, second),
            SyzygyNative.NormalizeTablePath(input));
    }

    [Fact]
    public void FilesystemRootSurvivesNormalization()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(root, SyzygyNative.NormalizeTablePath(root));
    }
}
