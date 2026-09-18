using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.ISO.Tests;

public sealed class IsoEstateCoverageTests
{
    private const string Estate = "/vault/Data/ISO639";

    [Fact]
    public async Task Installed_language_estate_has_no_unhandled_physical_artifacts()
    {
        if (!Directory.Exists(Estate)) return;

        var dec = new ISODecomposer();
        IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(
            await dec.DescribeArtifactsAsync(Estate, DecomposerOptions.Default));

        string[] unsupported = graph.Artifacts
            .Where(static artifact =>
                artifact.Disposition == IngestArtifactDisposition.Unsupported)
            .Select(static artifact => artifact.RelativePath)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unsupported.Length == 0,
            "Installed ISO/IANA/CLDR/Glottolog estate still has unhandled physical artifacts:\n"
            + string.Join("\n", unsupported));
    }
}
