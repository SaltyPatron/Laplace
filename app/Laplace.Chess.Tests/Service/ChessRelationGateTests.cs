using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// The decomposer contract's sharp edge (GH #577, the UD HAS_POS 0xC0000005 class): every
// relation a lane emits MUST be declared in its manifest. This gate runs a real game through
// BOTH chess lanes (recorder + analyzer, exercising header facts, reusable OUTCOME projections,
// motifs and opening classification) and asserts every attested relation id resolves to a
// declared name.
[Trait("Tier", "fast")]
public sealed class ChessRelationGateTests
{
    private const string Game =
        "[Event \"Gate\"]\n[Site \"https://lichess.org/AbCdEfGh\"]\n"
        + "[White \"Alice\"]\n[Black \"Bob\"]\n[WhiteElo \"2500\"]\n[BlackElo \"2400\"]\n"
        + "[Date \"2024.01.01\"]\n[Result \"1-0\"]\n[TimeControl \"60+0\"]\n"
        + "[Termination \"Normal\"]\n[ECO \"C20\"]\n\n"
        + "1. e4 {+0.28/12 0.95s} e5 {-0.21/14 1.02s} 2. Qh5?! {+0.35/13 0.98s} "
        + "Nc6 {-0.30/15 3.50s} 3. Bc4 {+1.05/10 0.05s} Nf6?? {+8.41/12 1.00s} "
        + "4. Qxf7# {+M0/5 0.10s} 1-0\n";

    [Fact]
    public void RecorderAndAnalyzer_EmitOnlyDeclaredRelations()
    {
        var declared = new Dictionary<Hash128, string>();
        foreach (var name in ChessSeedManifest.Relations)
            declared[RelationTypeRegistry.RelationTypeId(name)] = name;

        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/relation-gate");
        ChessPgnDecomposer.RecordGame(parsed, b);
        ChessAnalyze.DeriveFromParsed(b, parsed);
        var change = b.SetInputUnitsConsumed(1).Build();

        // Substrate META-TYPES are a different category from relations and must not be
        // declared in the relation manifest: they are minted inline, never entered in
        // relation_types.toml, never given a highway bit, and therefore never folded
        // (FileEntity.MetadataRelationTypeId, LayerCompletion's HasLayerCompleted, and the
        // chess analysis watermark). Verified live: HasFileMetadata 209 attestations / 0
        // consensus; HasLayerCompleted/2 8,995 / 0.
        //
        // The gate keeps its teeth because the exemption is not a name list: the id counts
        // only if THIS change also declares it as a meta-type entity. A typo'd or
        // undeclared relation still has nothing declaring it and still fails.
        var metaTypes = change.Entities
            .Where(e => e.TypeId == BootstrapIntentBuilder.RelationTypeMetaTypeId)
            .Select(e => e.Id)
            .ToHashSet();

        Assert.NotEmpty(change.Attestations);
        var undeclared = change.Attestations
            .Select(a => a.TypeId)
            .Distinct()
            .Where(t => !declared.ContainsKey(t) && !metaTypes.Contains(t))
            .Select(t => t.ToString())
            .ToList();
        Assert.True(undeclared.Count == 0,
            "emitted relation type ids with no ChessSeedManifest.Relations declaration "
            + $"(the 0xC0000005 class): {string.Join(", ", undeclared)}");
    }

    [Fact]
    public void Gate_ExercisesTheRiskyEmitters()
    {
        // The gate above is only as strong as the paths it drives. Pin that the two
        // reusable think-class outcome fires while occurrence projections remain absent.
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/relation-gate");
        ChessPgnDecomposer.RecordGame(parsed, b);
        ChessAnalyze.DeriveFromParsed(b, parsed);
        var change = b.SetInputUnitsConsumed(1).Build();

        var alias = RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS");
        Assert.Contains(change.Attestations, a => a.TypeId == alias);
        var thinkClasses = new[] { "rushed", "normal", "deep" }
            .Select(ContentEmitter.RootId)
            .Where(static id => id is not null)
            .Select(static id => id!.Value)
            .ToHashSet();
        Assert.Contains(change.Attestations,
            a => a.TypeId == ChessVocabulary.OutcomeType && thinkClasses.Contains(a.SubjectId));

        foreach (var relation in new[]
                 {
                     "MOVE", "HAS_CLOCK", "HAS_EVAL_TOKEN", "HAS_THINK_CLASS", "MOVE_QUALITY"
                 })
        {
            var typeId = RelationTypeRegistry.RelationTypeId(relation);
            Assert.DoesNotContain(change.Attestations, a => a.TypeId == typeId);
        }
    }
}
