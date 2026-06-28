using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// Fast lock-in for the native ETL marshalling structs. A [MarshalAs] string field makes the struct
/// NON-blittable, so the EtlSessionOpen &amp;cfg / fixed(EdgeRules) path blits the MANAGED layout — a String
/// object reference where native reads a char* — passing garbage (the suspected ConceptNet/Atomic2020
/// native-ingest no-op). These assert the structs are blittable AND match the native ABI sizes (config 112,
/// edge rule 16), which is exactly what the bug violated.
/// </summary>
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
