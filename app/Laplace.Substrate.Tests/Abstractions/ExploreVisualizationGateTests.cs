using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The Explore 2-D and 3-D force renderers mutate their graphData objects in place.
/// A shared object graph lets the 2-D simulation write planar coordinates that the
/// 3-D simulation later inherits. Three/WebGL colors also belong to the UI palette;
/// renderer-local legacy colors must not survive a product reskin.
/// </summary>
public class ExploreVisualizationGateTests
{
    private static string Root => TypeIdLawTests.FindRepoRootPublic();

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void ConsensusGraph_UsesDimensionPrivateVolumetricState()
    {
        var graph = Read("web", "src", "explore", "graph", "ConsensusGraph.tsx");

        Assert.Contains("graphForDimension(baseData, dim, centerId)", graph);
        Assert.Contains("volumetricSeed(node.id, node.hop)", graph);
        Assert.Contains("z * radius", graph);
        Assert.Contains("links: base.links.map((link) => ({ ...link }))", graph);
        Assert.DoesNotContain("const data = useMemo(\n    () => (web ? fromWeb", graph);
    }

    [Fact]
    public void ThreeRenderers_UseSharedUiVisualizationTokens_NotLegacyPalette()
    {
        var graph = Read("web", "src", "explore", "graph", "ConsensusGraph.tsx");
        var glome = Read("web", "src", "explore", "glome", "GlomeCanvas.tsx");
        var palette = Read("web", "src", "explore", "visualizationPalette.ts");
        var theme = Read("web", "src", "ui", "theme.css");

        Assert.Contains("visualizationPalette()", graph);
        Assert.Contains("visualizationPalette()", glome);
        Assert.Contains("--viz-signal: #69d9d1", theme);
        Assert.Contains("--viz-steel: #8fc4e2", theme);
        Assert.Contains("--viz-error: #ff8f9b", theme);
        Assert.Contains("--viz-text: #eef6f9", theme);
        Assert.Contains("--viz-muted: #b9cad4", theme);
        Assert.Contains("--viz-bg: #0a2638", theme);
        Assert.Contains("token('--viz-signal'", palette);

        foreach (var retired in new[] { "#4f8cff", "#3ecf8e", "#e8b339", "#9b7bff", "#f07178", "#0b1220" })
        {
            Assert.DoesNotContain(retired, graph, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(retired, glome, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GlomeRibbon_UsesTheSameLiveProjectionAsItsNodes()
    {
        var tab = Read("web", "src", "explore", "entity", "tabs", "GlomeTab.tsx");
        var canvas = Read("web", "src", "explore", "glome", "GlomeCanvas.tsx");

        Assert.DoesNotContain("placementBallPos", tab);
        Assert.DoesNotContain("packedDisplayPos", tab);
        Assert.DoesNotContain("trajectoryPoints={placementTrajectory", tab);
        Assert.Contains(".map((n) => project(n, projection, xmAngle))", canvas);
        Assert.Contains("`${projection}:${xmDegrees}", canvas);
    }
}
