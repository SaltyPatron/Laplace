using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.OMW;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Tests.OMW;

/// <summary>
/// GH #1027 — every placement must have an entity.
///
/// Measured on a fresh database mid-OMW: 1,713,165 physicalities against
/// 1,460,249 entities, a surplus of 252,916 that scales linearly with input
/// (~17% of entities: 59k at 5.5% of the corpus, 253k at 26%). Every dangling
/// row is a UNIQUE entity at exactly 1:1, type=Content, and splits cleanly by
/// size — dangling n_constituents >= 9, healthy &lt;= 7. UnicodeDecomposer runs the
/// harmless direction (-1,081), so it is not universal.
///
/// Eight code-reading hypotheses were disproved: both compose emitters,
/// materialize_phys, ContentTierSpine, content_witness_batch (which emits entity
/// and placement atomically under one dedup), the apply-side presence skip
/// (APPLY_PRESENT_SKIPPED never fires), the 512-byte stackalloc branch, and the
/// drain's key-space mismatch — that last is a real latent bug but only reachable
/// on allocation failure.
///
/// So this measures instead of theorising. It drives real OMW rows through the
/// SAME GrammarEntityBuilder the ingest uses, with no database and no ingest
/// mutex, and asserts the invariant directly. If it fails, the surplus is
/// produced at compose/build time and the row that produces it is named. If it
/// passes, the surplus enters after Build and the search moves downstream —
/// either answer is worth more than another reading of the source.
/// </summary>
public sealed class OmwPlacementEntityParityTests(ITestOutputHelper output)
{
    private const string WnsDir = "/vault/Data/omw/wns";

    [Fact]
    public void EveryComposedPlacementHasAStagedEntity()
    {
        if (!Directory.Exists(WnsDir))
        {
            output.WriteLine($"skipped: {WnsDir} not present");
            return;
        }

        CodepointPerfcache.LoadDefault();
        var omw = EtlManifest.Get("omw");

        string? tab = OMWTabFiles.EnumerateTabFiles(WnsDir, langs: null)
            .OrderBy(p => p).FirstOrDefault();
        Assert.NotNull(tab);

        long rows = 0, totalEnt = 0, totalPhys = 0, orphanRows = 0, orphanPlacements = 0;
        int minOrphanNc = int.MaxValue, maxOrphanNc = 0;

        var stream = GrammarFileRecordStream.ForSource(
            tab!, omw, line => line.Length > 0 && line[0] != (byte)'#');

        foreach (var record in stream.RecordsAsync(default).ToBlockingEnumerable())
        {
            if (rows >= 2000) break;
            rows++;
            var ast = record.Ast;
            try
            {
                var geb = new GrammarEntityBuilder(
                    record.LineUtf8, ast, omw.SourceId, omw.Modality.GrammarId);
                var (ents, phys, _, _) = geb.Build(1.0);

                totalEnt += ents.Length;
                totalPhys += phys.Length;

                var staged = new HashSet<Hash128>(ents.Length);
                foreach (var e in ents) staged.Add(e.Id);

                int orphansHere = 0;
                foreach (var p in phys)
                {
                    if (staged.Contains(p.EntityId)) continue;
                    orphansHere++;
                    if (p.NConstituents < minOrphanNc) minOrphanNc = p.NConstituents;
                    if (p.NConstituents > maxOrphanNc) maxOrphanNc = p.NConstituents;
                }
                if (orphansHere > 0)
                {
                    orphanRows++;
                    orphanPlacements += orphansHere;
                }
            }
            finally
            {
                ast.Dispose();
            }
        }

        output.WriteLine(
            $"rows={rows} entities={totalEnt} physicalities={totalPhys} "
            + $"surplus={totalPhys - totalEnt} orphan_rows={orphanRows} "
            + $"orphan_placements={orphanPlacements} "
            + $"orphan_nconst={(orphanPlacements > 0 ? $"{minOrphanNc}..{maxOrphanNc}" : "-")}");

        Assert.True(rows > 0, "no OMW records read; the assertion would be vacuous");
        Assert.Equal(0, orphanPlacements);
    }

    /// THE WITNESS STAGE — the layer neither test above exercises.
    ///
    /// FullBuilderDrainKeepsPlacementsUnderEntities reported staged_ent=0
    /// staged_phys=0, because it never ran OMWGrammarWitness.WalkRow. That is
    /// what drives ContentTierSpine into builder.ContentStage — the native
    /// IntentStage — which is the SECOND dedup universe the apply COPYs beside
    /// the managed arrays. Managed rows dedup on _seenEntities/_seenPhysicalities;
    /// staged rows dedup on intent_stage_witness_*. Nothing reconciles the two.
    ///
    /// So this runs the real witness over real rows and totals BOTH universes.
    /// It is the last layer above the database, and the database is where the
    /// surplus was measured (1,713,165 placements against 1,460,249 entities,
    /// ~17%, scaling linearly with input).
    [Fact]
    public void WitnessDrainKeepsPlacementsUnderEntities()
    {
        if (!Directory.Exists(WnsDir)) { output.WriteLine($"skipped: {WnsDir} absent"); return; }

        CodepointPerfcache.LoadDefault();
        // The witness resolves a language id per row, so the ISO-639 table has to
        // be loaded or WalkRow throws before it emits anything.
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
        var omw = EtlManifest.Get("omw");
        string? tab = OMWTabFiles.EnumerateTabFiles(WnsDir, langs: null)
            .OrderBy(p => p).FirstOrDefault();
        Assert.NotNull(tab);

        // Same construction the ingest uses: OMWDecomposer.CreateHandlerForFile
        // builds `new OMWGrammarWitness(OmwIngestSupport.LangFromLabel(fileLabel))`.
        var witness = new OMWGrammarWitness(OMWTabFiles.FileLang(tab!));
        var builder = new SubstrateChangeBuilder(omw.SourceId, "g1027-witness");
        long rows = 0;

        var stream = GrammarFileRecordStream.ForSource(
            tab!, omw, line => line.Length > 0 && line[0] != (byte)'#');

        foreach (var record in stream.RecordsAsync(default).ToBlockingEnumerable())
        {
            if (rows >= 2000) break;
            var ast = record.Ast;
            try
            {
                var geb = new GrammarEntityBuilder(
                    record.LineUtf8, ast, omw.SourceId, omw.Modality.GrammarId);
                var (ents, phys, atts, root) = geb.Build(1.0);
                foreach (var e in ents) builder.AddEntity(e);
                foreach (var p in phys) builder.AddPhysicality(p);
                foreach (var a in atts) builder.AddAttestation(a);

                witness.WalkRow(
                    new GrammarComposeContext(record.LineUtf8, ast, root, null),
                    new RowContext((int)rows, 1),
                    builder);
                rows++;
            }
            finally { ast.Dispose(); }
        }

        var change = builder.Build();
        int stagedEnt = 0, stagedPhys = 0;
        if (!change.IntentStages.IsDefault)
        {
            foreach (var st in change.IntentStages)
            {
                stagedEnt += st.EntityCount;
                stagedPhys += st.PhysicalityCount;
            }
        }

        long ent = change.Entities.Length + stagedEnt;
        long ph = change.Physicalities.Length + stagedPhys;
        output.WriteLine(
            $"rows={rows} managed_ent={change.Entities.Length} managed_phys={change.Physicalities.Length} "
            + $"staged_ent={stagedEnt} staged_phys={stagedPhys} "
            + $"TOTAL_ent={ent} TOTAL_phys={ph} surplus={ph - ent}");

        Assert.True(rows > 0, "no OMW records read; the assertion would be vacuous");
        Assert.True(ph <= ent, $"{ph - ent} placement(s) beyond entities after the witness drain");
    }

    /// The build-level parity above holds, so the surplus enters DOWNSTREAM of
    /// Build. SubstrateChange carries rows from TWO separate dedup universes: the
    /// managed ImmutableArrays (deduped by _seenEntities/_seenPhysicalities) and
    /// the native IntentStages (deduped by intent_stage_witness_*). The apply
    /// COPYs both. This measures that boundary the same way — real OMW rows, real
    /// witness, real builder, no database.
    [Fact]
    public void FullBuilderDrainKeepsPlacementsUnderEntities()
    {
        if (!Directory.Exists(WnsDir))
        {
            output.WriteLine($"skipped: {WnsDir} not present");
            return;
        }

        CodepointPerfcache.LoadDefault();
        var omw = EtlManifest.Get("omw");
        string? tab = OMWTabFiles.EnumerateTabFiles(WnsDir, langs: null)
            .OrderBy(p => p).FirstOrDefault();
        Assert.NotNull(tab);

        var builder = new SubstrateChangeBuilder(omw.SourceId, "g1027-probe");
        long rows = 0;

        var stream = GrammarFileRecordStream.ForSource(
            tab!, omw, line => line.Length > 0 && line[0] != (byte)'#');

        foreach (var record in stream.RecordsAsync(default).ToBlockingEnumerable())
        {
            if (rows >= 2000) break;
            rows++;
            var ast = record.Ast;
            try
            {
                var geb = new GrammarEntityBuilder(
                    record.LineUtf8, ast, omw.SourceId, omw.Modality.GrammarId);
                var (ents, phys, atts, _) = geb.Build(1.0);
                foreach (var e in ents) builder.AddEntity(e);
                foreach (var p in phys) builder.AddPhysicality(p);
                foreach (var a in atts) builder.AddAttestation(a);
            }
            finally { ast.Dispose(); }
        }

        int stagedEnt = 0, stagedPhys = 0;
        var change = builder.Build();
        foreach (var st in change.IntentStages.IsDefault ? [] : change.IntentStages)
        {
            stagedEnt += st.EntityCount;
            stagedPhys += st.PhysicalityCount;
        }

        long ent = change.Entities.Length + stagedEnt;
        long ph = change.Physicalities.Length + stagedPhys;

        output.WriteLine(
            $"rows={rows} managed_ent={change.Entities.Length} managed_phys={change.Physicalities.Length} "
            + $"staged_ent={stagedEnt} staged_phys={stagedPhys} "
            + $"TOTAL_ent={ent} TOTAL_phys={ph} surplus={ph - ent}");

        Assert.True(rows > 0, "no OMW records read; the assertion would be vacuous");
        Assert.True(ph <= ent,
            $"{ph - ent} placement(s) beyond entities after the full builder drain");
    }
}
