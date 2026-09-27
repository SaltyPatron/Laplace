using System.Runtime.InteropServices;
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
