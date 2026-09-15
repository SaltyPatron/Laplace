using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Operational;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Tests.Operational;

public sealed class OperationalTaskShapeTests
{
    private static readonly Hash128 ParseId = Hash128.OfCanonical("test/task-shape/exemplar");
    private static readonly Hash128 Predicate = RelationTypeRegistry.Resolve("CAUSES").Id;
    private static readonly Hash128 TokenRef = Laplace.Decomposers.UD.UdParseStructure.TokenRefId("2");

    static OperationalTaskShapeTests()
    {
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    [Fact]
    public async Task NativeSourceAdmissionRetainsExactShapeAndSourceScopedDeclarations()
    {
        string directory = Path.Combine(Path.GetTempPath(), "task-shape-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "shape.json");
            string json = Source(Predicate, EntityTypeRegistry.CodeConcept);
            await File.WriteAllTextAsync(path, json);
            var record = await OperationalDecomposer.ReadContractAsync(path, "shape.json");
            Assert.Equal(Encoding.UTF8.GetBytes(json), record.Utf8);
            Assert.NotNull(record.StructureWitness);
            var handler = new GrammarComposeHandler(OperationalSource.SourceId, SourceTrust.SubstrateMandate, null);
            using var unit = handler.CreateDeferredUnit(record);
            var builder = new SubstrateChangeBuilder(OperationalSource.SourceId, "shape-source");
            Hash128 file = unit.DrainInto(builder, SourceTrust.SubstrateMandate, null);
            handler.WalkWitness(record, file, builder, unit);
            var change = builder.Build();
            try
            {
                using var ast = GrammarDecomposer.Parse(record.Utf8, "json");
                var declared = OperationalTaskShapeWitness.Read(ast, record.Utf8);
                // Borrowed navigation must leave the original native source AST alive.
                Assert.True(ast.NodeCount > 0);
                var physicality = Assert.Single(change.Physicalities,
                    p => p.EntityId == declared.Id && p.Type == PhysicalityType.ParseStructure);
                Assert.Equal(declared.Constituents, Trajectory.Constituents(physicality.TrajectoryXyzm!));
                Assert.DoesNotContain(change.Physicalities,
                    p => p.EntityId == declared.Id && p.Type == PhysicalityType.Content);
                Assert.Contains(change.Attestations, a => a.SubjectId == ParseId
                    && a.TypeId == OperationalSource.ExampleOfTypeId && a.ObjectId == declared.Id
                    && a.SourceId == OperationalSource.SourceId && a.ContextId == file);
                Assert.Contains(change.Attestations, a => a.SubjectId == declared.Id
                    && a.TypeId == OperationalSource.CallsTypeId && a.ObjectId == Predicate
                    && a.SourceId == OperationalSource.SourceId && a.ContextId == file);
                Assert.Contains(change.Attestations, a => a.SubjectId == declared.Id
                    && a.TypeId == OperationalSource.InputTypeId && a.ObjectId == declared.Slots[0].Id
                    && a.SourceId == OperationalSource.SourceId && a.ContextId == file);
            }
            finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CanonicalShapeDependsOnDeclaredIdsRatherThanJsonPresentation()
    {
        string source = Source(Predicate, EntityTypeRegistry.CodeConcept);
        var first = Read(source);
        var reformatted = Read(source.Replace("\n", " ", StringComparison.Ordinal));
        Assert.Equal(first.Id, reformatted.Id);
        Assert.Equal(first.Id, Read(source.Replace("\"schema\"", "\"sche\\u006da\"", StringComparison.Ordinal)).Id);
        Assert.Equal(first.Id, Read(source.Replace("\"exemplar_token_ref_id\"", "\"exemplar_token_ref_\\u0069d\"", StringComparison.Ordinal)).Id);
        Assert.NotEqual(first.Id, Read(Source(RelationTypeRegistry.Resolve("HAS_PART").Id,
            EntityTypeRegistry.CodeConcept)).Id);
        Assert.NotEqual(first.Id, Read(Source(Predicate, EntityTypeRegistry.SourceReference)).Id);
    }

    [Fact]
    public void InvalidOrAmbiguousSourceDeclarationsFailInsteadOfDroppingFields()
    {
        string source = Source(Predicate, EntityTypeRegistry.CodeConcept);
        Assert.Throws<InvalidDataException>(() => Read(source.Replace("token-slots/v1", "token-slots/v2")));
        Assert.Throws<InvalidDataException>(() => Read(source.Replace(Hex(ParseId), "not-an-identity")));
        Assert.Throws<InvalidDataException>(() => Read(source.Replace(Hex(ParseId), new string('0', 32))));
        foreach (Hash128 reserved in new[] { OperationalTaskShapeWitness.SchemaId,
            OperationalTaskShapeWitness.SlotSchemaId, OperationalTaskShapeWitness.SlotsEndId })
            Assert.Throws<InvalidDataException>(() => Read(source.Replace(Hex(ParseId), Hex(reserved))));
        Assert.Throws<InvalidDataException>(() => Read(source.Replace("\"predicate_id\":", "\"ignored\": 1, \"predicate_id\":")));
        Assert.Throws<InvalidDataException>(() => Read(source.Replace("\"slots\":", "\"predicate_id\": \"" + Hex(Predicate) + "\", \"slots\":")));
        string slot = $$"""{"exemplar_token_ref_id":"{{Hex(TokenRef)}}","accepted_entity_type_id":"{{Hex(EntityTypeRegistry.CodeConcept)}}"}""";
        string duplicate = $$"""{"schema":"{{OperationalTaskShapeWitness.Schema}}","exemplar_parse_id":"{{Hex(ParseId)}}","predicate_id":"{{Hex(Predicate)}}","slots":[{{slot}},{{slot}}]}""";
        Assert.Throws<InvalidDataException>(() => Read(duplicate));
        Assert.Throws<InvalidDataException>(() => Read(source[..^1]));
        Assert.Throws<InvalidDataException>(() => Read("null " + source));
        Assert.Throws<InvalidDataException>(() => Read(source + " " + source));
    }

    [Fact]
    public async Task CanonicalPlacementIsIndependentOfSourceFormattingAndPropertyOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "task-shape-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string original = Source(Predicate, EntityTypeRegistry.CodeConcept);
            string reordered = $$"""
                {"slots":[{"accepted_entity_type_id":"{{Hex(EntityTypeRegistry.CodeConcept).ToUpperInvariant()}}","exemplar_token_ref_id":"{{Hex(TokenRef)}}"}],"predicate_id":"{{Hex(Predicate)}}","schema":"{{OperationalTaskShapeWitness.Schema}}","exemplar_parse_id":"{{Hex(ParseId)}}"}
                """;
            var first = await Admit(original, "original.json");
            foreach (var (json, name) in new[]
            {
                (original.Replace("\n", " ", StringComparison.Ordinal), "reformatted.json"),
                (reordered, "reordered.json"),
            })
            {
                var next = await Admit(json, name);
                Assert.NotEqual(first.File, next.File);
                Assert.Equal(first.Shape.EntityId, next.Shape.EntityId);
                Assert.Equal(first.Shape.Id, next.Shape.Id);
                Assert.Equal(first.Shape.TrajectoryXyzm, next.Shape.TrajectoryXyzm);
                Assert.Equal(first.Shape.CoordX, next.Shape.CoordX);
                Assert.Equal(first.Shape.CoordY, next.Shape.CoordY);
                Assert.Equal(first.Shape.CoordZ, next.Shape.CoordZ);
                Assert.Equal(first.Shape.CoordM, next.Shape.CoordM);
                Assert.Equal(first.Shape.HilbertIndex, next.Shape.HilbertIndex);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }

        async Task<(Hash128 File, PhysicalityRow Shape)> Admit(string json, string name)
        {
            string path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path, json);
            var record = await OperationalDecomposer.ReadContractAsync(path, name);
            var handler = new GrammarComposeHandler(OperationalSource.SourceId, SourceTrust.SubstrateMandate, null);
            using var unit = handler.CreateDeferredUnit(record);
            var builder = new SubstrateChangeBuilder(OperationalSource.SourceId, "shape-placement");
            Hash128 file = unit.DrainInto(builder, SourceTrust.SubstrateMandate, null);
            handler.WalkWitness(record, file, builder, unit);
            var change = builder.Build();
            try
            {
                return (file, Assert.Single(change.Physicalities,
                    p => p.Type == PhysicalityType.ParseStructure));
            }
            finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
        }
    }

    private static OperationalTaskShapeWitness.Definition Read(string source)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        using var ast = GrammarDecomposer.Parse(bytes, "json");
        return OperationalTaskShapeWitness.Read(ast, bytes);
    }

    private static string Source(Hash128 predicate, Hash128 acceptedType) => $$"""
        {
          "schema": "{{OperationalTaskShapeWitness.Schema}}",
          "exemplar_parse_id": "{{Hex(ParseId)}}",
          "predicate_id": "{{Hex(predicate)}}",
          "slots": [
            {"exemplar_token_ref_id": "{{Hex(TokenRef)}}", "accepted_entity_type_id": "{{Hex(acceptedType)}}"}
          ]
        }
        """;

    private static string Hex(Hash128 id) => Convert.ToHexString(id.ToBytes()).ToLowerInvariant();
}
