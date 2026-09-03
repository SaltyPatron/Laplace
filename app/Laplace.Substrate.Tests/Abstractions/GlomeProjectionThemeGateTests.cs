using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class GlomeProjectionThemeGateTests
{
    private static string Root => TypeIdLawTests.FindRepoRootPublic();

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void PlacementProjection_PreservesMAndRadiusDepth_AndNodesStayPainted()
    {
        var canvas = Read("web", "src", "explore", "glome", "GlomeCanvas.tsx");

        Assert.Contains("const xmM = x * sx + m * cx", canvas, StringComparison.Ordinal);
        Assert.Contains("const zmZ = z * cz - xmM * sz", canvas, StringComparison.Ordinal);
        Assert.Contains("const radius4", canvas, StringComparison.Ordinal);
        Assert.Contains("const displayRadius = SHELL * Math.max(0.02, radius4)", canvas, StringComparison.Ordinal);
        Assert.Contains("<color attach=\"background\"", canvas, StringComparison.Ordinal);
        Assert.Contains("<meshBasicMaterial vertexColors toneMapped={false} />", canvas, StringComparison.Ordinal);
        Assert.Contains("material.needsUpdate = true", canvas, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "return [rotatedX * SHELL, y * SHELL, z * SHELL]",
            canvas,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "meshStandardMaterial vertexColors emissiveIntensity",
            canvas,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_FollowsSystemPreference_WithoutHardBlackVisualizationSurfaces()
    {
        var theme = Read("web", "src", "ui", "theme.css");
        var canvasCss = Read("web", "src", "explore", "glome", "GlomeCanvas.module.css");
        var tabCss = Read("web", "src", "explore", "entity", "tabs", "GlomeTab.module.css");

        Assert.Contains("@media (prefers-color-scheme: dark)", theme, StringComparison.Ordinal);
        Assert.Contains("--color-viz-bg: #e4edf2", theme, StringComparison.Ordinal);
        Assert.Contains("--color-viz-bg: #173b50", theme, StringComparison.Ordinal);
        Assert.Contains("linear-gradient(180deg, var(--color-bg-top)", theme, StringComparison.Ordinal);
        Assert.Contains("background: var(--color-viz-bg)", canvasCss, StringComparison.Ordinal);
        Assert.Contains("background: var(--color-viz-bg)", tabCss, StringComparison.Ordinal);
        Assert.DoesNotContain("#05070c", canvasCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#05070c", tabCss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlacementRibbon_IsProjectedByTheSameLiveRotationAsItsNodes()
    {
        var tab = Read("web", "src", "explore", "entity", "tabs", "GlomeTab.tsx");

        Assert.Contains("projection=\"placement\"", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("placementBallPos", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("placementTrajectory", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("trajectoryPoints={placementTrajectory", tab, StringComparison.Ordinal);
    }
}
