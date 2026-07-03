using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[Trait("Tier", "fast")]
public class EtlMarshallingTests
{
    [Fact]
    public void EtlConfigNative_IsBlittable()
        => Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<NativeInterop.EtlConfigNative>(),
            "EtlConfigNative must be blittable — a managed field corrupts the &cfg blit to native");

    [Fact]
    public void EtlEdgeRuleNative_IsBlittable()
        => Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<NativeInterop.EtlEdgeRuleNative>(),
            "EtlEdgeRuleNative must be blittable — a managed field corrupts the fixed(EdgeRules) blit");

    [Fact]
    public void EtlConfigNative_MatchesNativeAbiSize()
        => Assert.Equal(112, Marshal.SizeOf<NativeInterop.EtlConfigNative>());

    [Fact]
    public void EtlEdgeRuleNative_MatchesNativeAbiSize()
        => Assert.Equal(16, Marshal.SizeOf<NativeInterop.EtlEdgeRuleNative>());
}
