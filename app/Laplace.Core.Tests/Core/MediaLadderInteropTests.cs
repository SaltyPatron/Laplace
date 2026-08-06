using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests.Core;

/// <summary>
/// Interop against laplace_image_* / laplace_audio_* after the codepoint-floor
/// rewrite. Roots must come from native compose, never blake3 of recovery buffers.
/// </summary>
[Collection("Perfcache")]
public sealed class MediaLadderInteropTests
{
    [Fact]
    public void ImageRootId_IsDeterministic_AndNotPackagingBlake3()
    {
        // 1×1 white opaque — recovery bytes; identity is codepoint ladder.
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
        // Codepoint-floor tree has digit leaves + number/channel/pixel/… above;
        // private packed-RGBA ladder had ≥4 nodes for 1×1 — floor rewrite has more.
        Assert.True(tree!.NodeCount >= 4);
    }

    [Fact]
    public void MediaLadderKind_AbiMatchesHistoricModalityEnum()
    {
        // P/Invoke ladderKind arg still 1/2 until native rips laplace_modality_t.
        Assert.Equal(1, (int)MediaLadderKind.Image);
        Assert.Equal(2, (int)MediaLadderKind.Audio);
    }
}
