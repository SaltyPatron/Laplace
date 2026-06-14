using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Actually RUNS <see cref="ConceptAnchor.EmitSynset"/> — perfcache loaded, against the real CILI
/// — instead of trusting that "it compiles." Proves the offset→ILI→decomposed-anchor + IS_A path
/// produces a real entity, or fails loudly. Skips only when the CILI data isn't on the box.
/// </summary>
public class ConceptAnchorTests
{
    [Fact]
    public void EmitSynset_Runs_AndProducesDecomposedAnchorPlusIsA()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName))) return; // no data — skip

        CodepointPerfcache.LoadDefault(); // ConceptAnchor decomposes the ILI through the perfcache

        var source = Hash128.OfCanonical("substrate/source/test/wn-anchor/v1");
        var b = new SubstrateChangeBuilder(source, "test/concept-anchor", null,
            entityCapacity: 64, physicalityCapacity: 64, attestationCapacity: 64);

        // supermodel synset: offset 10676319, ss_type 'n' -> ILI i93445 -> decomposed anchor
        Hash128? id = ConceptAnchor.EmitSynset(b, 10676319, 'n', source, SourceTrust.StandardsDerived);

        Assert.NotNull(id);                                       // resolved + staged, not null
        Assert.Equal(id, ConceptAnchor.SynsetId(10676319, 'n'));  // id stable across emit and resolve

        // The decomposed ILI anchor + its codepoint constituents actually stage into the native
        // content stage — proving real staging, not a silent perfcache-not-loaded no-op. Content
        // rides the IntentStage ("no C# row materialization"), never change.Entities.
        Assert.True(b.ContentStage.EntityCount > 0, "decomposed ILI anchor must stage entities");

        var change = b.Build();
        var isA = RelationTypeRegistry.RelationTypeId("IS_A");
        Assert.Contains(change.Attestations, a =>                  // category is an IS_A attestation
            a.SubjectId == id!.Value && a.TypeId == isA && a.ObjectId == EntityTypeRegistry.WordNetSynset);
    }

    /// <summary>
    /// The de-blob keys the synset anchor on the RAW ss_type. A satellite adjective ('s') resolves
    /// to its own ILI anchor and is NOT reachable as a head adjective ('a') — which is exactly why
    /// the legacy s→a fold (in WordNet's NormPos and OMW's parser) silently dropped all 10,693
    /// satellite synsets out of cross-language convergence. WordNet and OMW now both pass raw
    /// ss_type straight into <see cref="ConceptAnchor.SynsetId"/>, so this is the shared chokepoint.
    /// </summary>
    [Fact]
    public void Satellite_ResolvesOnlyAsRawPos_FoldToAdjectiveWouldDropIt()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        string mapPath = Path.Combine(cili, IliMap.MapFileName);
        if (!File.Exists(mapPath)) return; // no data — skip

        CodepointPerfcache.LoadDefault();

        // Find a real satellite-adjective ('-s') synset offset straight from the CILI map.
        long satOffset = -1;
        foreach (var line in File.ReadLines(mapPath))
        {
            var op = line.AsSpan(line.IndexOf('\t') + 1).Trim();   // "OFFSET-pos"
            int d = op.LastIndexOf('-');
            if (d <= 0) continue;
            var posSpan = op[(d + 1)..];
            if (posSpan.Length != 1 || posSpan[0] != 's') continue;
            if (long.TryParse(op[..d], out satOffset)) break;
        }
        Assert.True(satOffset > 0, "expected a satellite synset in the CILI map");

        Assert.NotNull(ConceptAnchor.SynsetId(satOffset, 's')); // resolvable as the satellite it is …
        Assert.Null(ConceptAnchor.SynsetId(satOffset, 'a'));    // … and the old fold-to-'a' would miss it
    }
}
