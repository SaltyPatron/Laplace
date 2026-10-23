using System.Text;
using Laplace.Decomposers.Composition;
using Laplace.Decomposers.OMW;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class GrammarSpineConformanceTests
{
    [Fact]
    public void TsvSpan_ParsesConllUFields()
    {
        ReadOnlySpan<byte> line = "1\tform\t_\t_\t_\t_\t0\troot\t_\t_"u8;
        Assert.True(TsvSpan.TryField(line, 0, out var id));
        Assert.True(TsvSpan.TryField(line, 1, out var form));
        Assert.Equal("1", Encoding.UTF8.GetString(id));
        Assert.Equal("form", Encoding.UTF8.GetString(form));
    }

    /// <summary>
    /// Comments are stripped before the needle search, so a needle naming a base class is
    /// satisfied by code that declares it, not by a comment that mentions it.
    /// </summary>
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                if (i < source.Length) sb.Append('\n');
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
                continue;
            }
            sb.Append(source[i]);
        }
        return sb.ToString();
    }

    [Fact]
    public void CanonicalSourcesHaveNoParallelManagedRegistrations()
    {
        // Canonical corpora use the Native recipe engine, never a second managed
        // per-record provider registered under the same source name.
        Assert.Empty(EtlManifest.SourceOrder.Intersect(
            SeedIngestComposition.RegisteredSourceKeys, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void VendorDecomposers_DoNotAdmitSerializedGrammarRowsByConvenience()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var root = Path.Combine(repoRoot, "app", "Laplace.Decomposers");
        var text = string.Join('\n', Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(p => StripComments(File.ReadAllText(p))));

        Assert.DoesNotContain("new GrammarIngestHandler(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestPipelineDefaults.StructuredGrammar(", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OMW")]
    [InlineData("ConceptNet")]
    [InlineData("Tatoeba")]
    [InlineData("OpenSubtitles")]
    public void NativeCorpusNamespacesContainMetadataRatherThanParallelManagedParsers(string corpus)
    {
        var types = typeof(OMWSource).Assembly.GetTypes()
            .Where(type => !type.IsNested && type.Namespace == "Laplace.Decomposers." + corpus);
        var metadata = Assert.Single(types);
        Assert.Contains(typeof(ISeedSource), metadata.GetInterfaces());
    }
}
