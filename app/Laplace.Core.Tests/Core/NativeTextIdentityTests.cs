using System.Text;
using System.Buffers.Binary;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class NativeTextIdentityTests
{
    // Read from the separately built Engine's `laplace text` command over the
    // canonical a6540fe9... scalar cache and Native adfda017 (UAX boundaries),
    // not calculated by this test's Core.
    [Theory]
    [InlineData("abandon.01", "2d25f2d444594c0115ab1bdab872167b")]
    [InlineData("New York", "7400a4cd15dfbd2acd2ceebfbae264c9")]
    [InlineData("mississippi", "8e6d5bf8d0c8be55f1e5ae1f87465581")]
    public void TextMatchesIndependentEngineComposition(string text, string expected)
    {
        Assert.Equal(expected, Hash128.Text(text).ToHex());
        Assert.NotEqual(Hash128.Blake3(Encoding.UTF8.GetBytes(text)), Hash128.Text(text));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("mississippi")]
    [InlineData("aaaa")]
    [InlineData("abababab")]
    public void RepeatedSubstringsDoNotAddBoundariesInsideOneWord(string text)
    {
        var scalars = text.EnumerateRunes()
            .Select(scalar => Hash128.Blake3(Encoding.UTF8.GetBytes(scalar.ToString()))).ToArray();
        Assert.Equal(Hash128.Merkle(1, scalars), Hash128.Text(text));
    }

    [Fact]
    public void GraphemesPreserveCombiningMarksAndEmojiWithoutNormalization()
    {
        Hash128 Scalar(string value) => Hash128.Blake3(Encoding.UTF8.GetBytes(value));
        Assert.Equal(Hash128.Merkle(1, [Scalar("A"), Scalar("\u030a")]), Hash128.Text("A\u030a"));
        Assert.NotEqual(Hash128.Text("Å"), Hash128.Text("A\u030a"));
        Assert.Equal(Hash128.Merkle(1, [Scalar("👍"), Scalar("🏽")]), Hash128.Text("👍🏽"));
        Assert.Equal(Scalar("\0"), Hash128.Text("\0"));
    }

    [Fact]
    public void MalformedUtf16AndAbsentContentDoNotMintIds()
    {
        Assert.Throws<ArgumentNullException>(() => Hash128.Text(null!));
        Assert.Throws<ArgumentException>(() => Hash128.Text(""));
        Assert.Throws<EncoderFallbackException>(() => Hash128.Text("\ud800"));
        Assert.Throws<EncoderFallbackException>(() => Hash128.Text("\udfff"));
    }

    [Fact]
    public void WorkerContextsRemainIndependent()
    {
        string[] content = ["abandon.01", "i46360", "A\u030a", "New York", "mississippi", "👍🏽"];
        var expected = content.Select(Hash128.Text).ToArray();
        Parallel.For(0, 120, index =>
        {
            int item = index % content.Length;
            Assert.Equal(expected[item], Hash128.Text(content[item]));
        });
    }

    [Theory]
    [InlineData("WordNet_Synset")]
    [InlineData("FrameNet_Frame")]
    [InlineData("New York")]
    [InlineData("mississippi")]
    [InlineData("A\u030a 👍🏽\r\nNext line.\n\nA paragraph.")]
    public unsafe void RecordedTreeRecomposesEveryNodeAndPreservesSourceBytes(string content)
    {
        using var tree = TextDecomposer.Run(content);
        Assert.Equal(Hash128.Text(content), tree.GetNode(tree.NaturalUnitIndex()).Id);
        using var bytes = new MemoryStream();
        var floorPath = Environment.GetEnvironmentVariable("LAPLACE_TIER0")
            ?? throw new InvalidOperationException("Set LAPLACE_TIER0 to the canonical Native scalar cache.");
        using var floor = File.OpenRead(floorPath);
        Assert.Equal(1_114_112L * 64, floor.Length);
        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var node = tree.GetNode(i);
            if (node.Tier == 0)
            {
                bytes.Write(Encoding.UTF8.GetBytes(new Rune((int)node.Atom).ToString()));
                var expected = ScalarCoordinate(floor, node.Id);
                for (int axis = 0; axis < 4; axis++) Assert.Equal(expected[axis], node.Coord[axis]);
                continue;
            }
            var children = new Hash128[node.ChildCount];
            var sums = new Int128[4];
            for (uint child = 0; child < node.ChildCount; child++)
            {
                var value = tree.GetNode(node.FirstChildIdx + child);
                children[child] = value.Id;
                for (int axis = 0; axis < 4; axis++) sums[axis] += (long)Math.ScaleB(value.Coord[axis], 53);
            }
            Assert.Equal(node.Id, Hash128.Merkle(node.Tier, children));
            for (int axis = 0; axis < 4; axis++)
                Assert.Equal(Math.ScaleB((double)(long)(sums[axis] / node.ChildCount), -53), node.Coord[axis]);
        }
        Assert.Equal(Encoding.UTF8.GetBytes(content), bytes.ToArray());
    }

    private static double[] ScalarCoordinate(FileStream floor, Hash128 id)
    {
        Span<byte> key = stackalloc byte[16]; id.WriteBytes(key);
        Span<byte> record = stackalloc byte[64];
        long first = 0, end = floor.Length / 64;
        while (first < end)
        {
            long middle = first + (end - first) / 2;
            floor.Position = middle * 64; floor.ReadExactly(record);
            int comparison = record[..16].SequenceCompareTo(key);
            if (comparison < 0) first = middle + 1;
            else if (comparison > 0) end = middle;
            else
            {
                var coordinates = new double[4];
                for (int axis = 0; axis < 4; axis++)
                    coordinates[axis] = Math.ScaleB(BinaryPrimitives.ReadInt64LittleEndian(record.Slice(16 + axis * 8, 8)), -53);
                return coordinates;
            }
        }
        throw new InvalidDataException("The emitted scalar ID is absent from canonical tier0.bin.");
    }
}
