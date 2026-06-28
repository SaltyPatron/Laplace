using System.Collections.Generic;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// CanUseNative now admits IliSynset anchor sources (the native resolver is bit-identical, proven by
/// AnchorResolverParityTests) but must still exclude SenseKey/FrameCategory — those resolvers EMIT their
/// anchor entity (SenseAnchor.Emit/CategoryAnchor.Emit), which the native field-edge witness does not do.
/// </summary>
[Trait("Tier", "fast")]
public class NativeAnchorGateTests
{
    private static EtlSource Src(AnchorResolver anchor, int edgeRules) =>
        new(
            Name: "GateProbe_" + anchor + "_" + edgeRules,   // unique → not an EtlWitnessFactory bespoke name
            SourceId: Hash128.OfCanonical("gate/probe"),
            Layer: 3,
            TrustClassId: Hash128.OfCanonical("gate/trust"),
            Trust: 0.85,
            DataKey: "gate",
            Modality: new EtlModality("tsv"),
            NodeEdgeMap: edgeRules > 0
                ? new List<EdgeRule> { new(0, 1, "SENSE_OF", EdgeRoleKind.Anchor, EdgeRoleKind.Content) }
                : new List<EdgeRule>(),
            Anchor: anchor);

    [Fact]
    public void IliSynset_DeclarativeSource_IsNative() =>
        Assert.True(NativeGrammarIngest.CanUseNative(Src(AnchorResolver.IliSynset, 1)));

    [Fact]
    public void NoAnchor_DeclarativeSource_IsNative() =>
        Assert.True(NativeGrammarIngest.CanUseNative(Src(AnchorResolver.None, 1)));

    [Fact]
    public void SenseKey_StaysOnCSharpPath() =>
        Assert.False(NativeGrammarIngest.CanUseNative(Src(AnchorResolver.SenseKey, 1)));

    [Fact]
    public void FrameCategory_StaysOnCSharpPath() =>
        Assert.False(NativeGrammarIngest.CanUseNative(Src(AnchorResolver.FrameCategory, 1)));

    [Fact]
    public void NoEdgeRules_IsNotNative() =>
        Assert.False(NativeGrammarIngest.CanUseNative(Src(AnchorResolver.IliSynset, 0)));
}
