using System.Runtime.InteropServices;
using System.Buffers.Binary;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests.Core;

/// <summary>
/// Interop against laplace_image_* / laplace_audio_*: an image or audio root id comes
/// from native composition over existing atoms, never a BLAKE3 of the raw buffer.
/// </summary>
[Collection("Perfcache")]
public sealed class MediaLadderInteropTests
{
    [Theory]
    [InlineData(-42)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(255)]
    public void AudioRolesRetainCanonicalScalarHeightsInTreeAndCopy(short sample)
    {
        using var scalar = TextDecomposer.Run(sample.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var expected = scalar.GetNode(scalar.NaturalUnitIndex());
        using var tree = IntentStage.BuildAudioTree([sample, sample]);
        Assert.NotNull(tree);
        var nodes = Enumerable.Range(0, tree!.NodeCount).Select(i => tree.GetNode((uint)i)).ToArray();
        var samples = nodes.Where(n => n.Role == 1).ToArray();
        Assert.Equal(2, samples.Length);
        Assert.All(samples, node =>
        {
            Assert.Equal(expected.Id, node.Id);
            Assert.Equal(expected.Tier, node.Tier);
        });
        var window = Assert.Single(nodes, n => n.Role == 2);
        Assert.Equal(expected.Tier + 1, window.Tier);
        var track = Assert.Single(nodes, n => n.Role == 5);
        Assert.Equal(window.Id, track.Id);
        Assert.Equal(window.Tier, track.Tier);
        using var stage = IntentStage.New(32);
        Assert.True(stage.EmitAudioTree(tree, Hash128.Blake3("audio-height-receipt"u8), [], out var root));
        Assert.Equal(window.Id, root);
        var entities = ReadEntityHeights(stage);
        Assert.Equal((short)window.Tier, entities[window.Id]);
        if (expected.Tier != 0) Assert.Equal((short)expected.Tier, entities[expected.Id]);
        else Assert.DoesNotContain(expected.Id, entities.Keys);
    }

    [Fact]
    public void ImageChannelRolesDoNotAddHeightOrChangeTheSharedScalar()
    {
        using var scalar = TextDecomposer.Run("255");
        var expected = scalar.GetNode(scalar.NaturalUnitIndex());
        using var tree = IntentStage.BuildImageTree([255, 255, 255, 255], 1, 1);
        Assert.NotNull(tree);
        var nodes = Enumerable.Range(0, tree!.NodeCount).Select(i => tree.GetNode((uint)i)).ToArray();
        Assert.All(nodes.Where(n => n.Role is 1 or 2), node =>
        {
            Assert.Equal(expected.Id, node.Id);
            Assert.Equal(expected.Tier, node.Tier);
        });
        var pixel = Assert.Single(nodes, n => n.Role == 3);
        Assert.Equal(expected.Tier + 1, pixel.Tier);
        var image = Assert.Single(nodes, n => n.Role == 6);
        Assert.Equal(pixel.Id, image.Id);
        Assert.Equal(pixel.Tier, image.Tier);
        using var stage = IntentStage.New(32);
        Assert.True(stage.EmitImageTree(tree, Hash128.Blake3("image-height-receipt"u8), [], out var root));
        Assert.Equal(pixel.Id, root);
        var entities = ReadEntityHeights(stage);
        Assert.Equal((short)pixel.Tier, entities[pixel.Id]);
        Assert.Equal((short)expected.Tier, entities[expected.Id]);
    }

    private static Dictionary<Hash128, short> ReadEntityHeights(IntentStage stage)
    {
        var result = new Dictionary<Hash128, short>();
        byte[] copy = stage.EmitCopyBinary(IntentStageTable.Entities);
        int at = 19;
        while (BinaryPrimitives.ReadInt16BigEndian(copy.AsSpan(at, 2)) != -1)
        {
            Assert.Equal(3, BinaryPrimitives.ReadInt16BigEndian(copy.AsSpan(at, 2)));
            at += 2;
            Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(copy.AsSpan(at, 4)));
            var id = Hash128.FromBytes(copy.AsSpan(at + 4, 16));
            at += 20;
            Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(copy.AsSpan(at, 4)));
            result.Add(id, BinaryPrimitives.ReadInt16BigEndian(copy.AsSpan(at + 4, 2)));
            at += 6;
            int typeLength = BinaryPrimitives.ReadInt32BigEndian(copy.AsSpan(at, 4));
            at += 4 + typeLength;
        }
        return result;
    }

    [Fact]
    public void ImageRootId_IsDeterministic_AndNotPackagingBlake3()
    {
        // 1×1 white opaque pixel; the id is its composition, not these bytes.
        byte[] rgba = [0xFF, 0xFF, 0xFF, 0xFF];
        var a = IntentStage.ImageRootId(rgba, 1, 1);
        var b = IntentStage.ImageRootId(rgba, 1, 1);
        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.NotEqual(Hash128.Blake3(rgba), a!.Value);
    }

    [Fact]
    public void AudioRootId_IsDeterministic_AndNotPackagingBlake3()
    {
        short[] pcm = [-100, 0, 100, 200];
        var a = IntentStage.AudioRootId(pcm);
        var b = IntentStage.AudioRootId(pcm);
        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.NotEqual(Hash128.Blake3(MemoryMarshal.AsBytes(pcm.AsSpan())), a!.Value);
    }

    [Fact]
    public void BuildImageTree_EmitsNodes()
    {
        byte[] rgba = [0xAA, 0xBB, 0xCC, 0xDD];
        using var tree = IntentStage.BuildImageTree(rgba, 1, 1);
        Assert.NotNull(tree);
        // Digit leaves compose into numbers, channels and the pixel above them.
        Assert.True(tree!.NodeCount >= 4);
    }

    [Fact]
    public void MediaLadderKind_AbiMatchesHistoricModalityEnum()
    {
        // The P/Invoke ladderKind argument uses the native values 1 (image), 2 (audio).
        Assert.Equal(1, (int)MediaLadderKind.Image);
        Assert.Equal(2, (int)MediaLadderKind.Audio);
    }
}
