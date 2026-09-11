using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessLabStorageTests
{
    [Fact]
    public void PersistPgn_DefaultsFalse()
    {
        var config = new Dictionary<string, string>();
        Assert.False(ChessLabStorage.PersistPgn(config));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("garbage", false)]
    public void PersistPgn_RequiresExplicitTrue(string raw, bool expected)
    {
        var config = new Dictionary<string, string> { ["persistPgn"] = raw };
        Assert.Equal(expected, ChessLabStorage.PersistPgn(config));
    }

    [Fact]
    public void PersistPgn_CanDefaultTrueForNonIngestFetches()
    {
        var config = new Dictionary<string, string>();
        Assert.True(ChessLabStorage.PersistPgn(config, defaultValue: true));
    }

    [Fact]
    public void SpoolRoot_UsesExplicitEnvironmentOverride()
    {
        var prior = Environment.GetEnvironmentVariable("LAPLACE_CHESS_SPOOL_DIR");
        var expected = Path.Combine(Path.GetTempPath(), $"laplace-spool-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("LAPLACE_CHESS_SPOOL_DIR", expected);
        try
        {
            Assert.Equal(Path.GetFullPath(expected), ChessLabStorage.ResolveSpoolRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_SPOOL_DIR", prior);
        }
    }

    [Fact]
    public void PersistArtifact_MovesSpoolFileIntoExplicitArtifactDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"laplace-storage-test-{Guid.NewGuid():N}");
        var spool = Path.Combine(root, "spool");
        var lab = Path.Combine(root, "lab");
        Directory.CreateDirectory(spool);
        var source = Path.Combine(spool, "games.pgn");
        File.WriteAllText(source, "[Event \"test\"]\n");
        try
        {
            var target = ChessLabStorage.PersistArtifact(source, lab, "job", "games.pgn");
            Assert.False(File.Exists(source));
            Assert.True(File.Exists(target));
            Assert.Equal(Path.Combine(lab, "job", "games.pgn"), target);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
