using System.Text;
using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

public class SourceEntityIdConventionsTests
{
    private static string NewTempDir()
    {
        string d = Path.Combine(Path.GetTempPath(),
            "laplace-srcid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WriteModel(string dir, byte[] weights, string config)
    {
        File.WriteAllBytes(Path.Combine(dir, "model.safetensors"), weights);
        File.WriteAllText(Path.Combine(dir, "config.json"), config);
        return dir;
    }

    [Fact]
    public void Model_IdenticalContent_DifferentDirName_SameId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            var bytes = Encoding.ASCII.GetBytes(new string('W', 4096));
            WriteModel(a, bytes, "{\"hidden\":8}");
            WriteModel(b, bytes, "{\"hidden\":8}");

            Hash128? ida = SourceEntityIdConventions.ModelContentSourceId(a);
            Hash128? idb = SourceEntityIdConventions.ModelContentSourceId(b);

            Assert.NotNull(ida);
            Assert.Equal(ida, idb);
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Model_OneByteDifferent_DistinctId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            var bytes = Encoding.ASCII.GetBytes(new string('W', 4096));
            WriteModel(a, bytes, "{\"hidden\":8}");
            var flipped = (byte[])bytes.Clone();
            flipped[2048] ^= 0x01;
            WriteModel(b, flipped, "{\"hidden\":8}");

            Assert.NotEqual(SourceEntityIdConventions.ModelContentSourceId(a),
                            SourceEntityIdConventions.ModelContentSourceId(b));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Model_DifferentConfigSameWeights_DistinctId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            var bytes = Encoding.ASCII.GetBytes(new string('W', 4096));
            WriteModel(a, bytes, "{\"rope_theta\":10000}");
            WriteModel(b, bytes, "{\"rope_theta\":500000}");

            Assert.NotEqual(SourceEntityIdConventions.ModelContentSourceId(a),
                            SourceEntityIdConventions.ModelContentSourceId(b));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Model_NoWeightFiles_ReturnsNull()
    {
        var d = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(d, "config.json"), "{\"hidden\":8}");
            Assert.Null(SourceEntityIdConventions.ModelContentSourceId(d));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Model_ChunkBoundary_DeterministicAcrossSizes()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            var bytes = new byte[70 * 1024 * 1024 + 12345];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + 7);
            WriteModel(a, bytes, "{\"hidden\":8}");
            WriteModel(b, bytes, "{\"hidden\":8}");
            Assert.Equal(SourceEntityIdConventions.ModelContentSourceId(a),
                         SourceEntityIdConventions.ModelContentSourceId(b));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Text_BomAndCrlfVariant_SameId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(a, "corpus.txt"),
                Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n"));
            var withBomCrlf = new List<byte> { 0xEF, 0xBB, 0xBF };
            withBomCrlf.AddRange(Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma\r\n"));
            File.WriteAllBytes(Path.Combine(b, "corpus.txt"), withBomCrlf.ToArray());

            Hash128 ida = SourceEntityIdConventions.NormalizedTextSourceId(
                "substrate/source/test-text/v1", new[] { Path.Combine(a, "corpus.txt") });
            Hash128 idb = SourceEntityIdConventions.NormalizedTextSourceId(
                "substrate/source/test-text/v1", new[] { Path.Combine(b, "corpus.txt") });

            Assert.Equal(ida, idb);
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void Text_DifferentContent_DistinctId()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(a, "corpus.txt"), "alpha\nbeta\n");
            File.WriteAllText(Path.Combine(b, "corpus.txt"), "alpha\ndelta\n");
            Assert.NotEqual(
                SourceEntityIdConventions.NormalizedTextSourceId(
                    "substrate/source/test-text/v1", new[] { Path.Combine(a, "corpus.txt") }),
                SourceEntityIdConventions.NormalizedTextSourceId(
                    "substrate/source/test-text/v1", new[] { Path.Combine(b, "corpus.txt") }));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }
}
