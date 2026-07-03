using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class Pwn16CiliIntegrationTests
{
    [Fact]
    public void Pwn16_And_Pwn30_Resolve_MapNet_Test_Offset_To_Different_Anchors()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        string pwn16Path = Path.Combine(cili, "older-wn-mappings", "ili-map-pwn16.tab");
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName)) || !File.Exists(pwn16Path))
            return;

        string? saved = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR");
        Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", cili);
        SourceEntityIdConventions.ResetIliMapCacheForTests();
        try
        {
            CodepointPerfcache.LoadDefault();
            Hash128? syn16 = ConceptAnchor.SynsetId(2_814_860, 'n', SourceEntityIdConventions.MultiWordNetWnVersion);
            Hash128? syn30 = ConceptAnchor.SynsetId(2_814_860, 'n');
            Assert.NotNull(syn16);
            Assert.NotNull(syn30);
            Assert.NotEqual(syn16, syn30);

            Hash128? resolved = SourceEntityIdConventions.ResolveSynsetAnchor(
                "n#02814860", SourceEntityIdConventions.MultiWordNetWnVersion);
            Assert.Equal(syn16, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", saved);
            SourceEntityIdConventions.ResetIliMapCacheForTests();
        }
    }
}
