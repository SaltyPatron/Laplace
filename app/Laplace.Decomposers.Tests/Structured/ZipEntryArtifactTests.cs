using System.IO.Compression;
using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Structured;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Decomposers.Tests.Structured;

public class ZipEntryArtifactTests
{
    // A zip artifact's declared entries are each parsed as their own file (UCA's
    // CollationTest.zip holds the NON_IGNORABLE and SHIFTED orderings).
    [Fact]
    public async Task Declared_zip_entries_are_each_parsed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-zip-entries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "names.recipe.json"), """
                {"authority": "Test/Zip", "release": "1", "provider": "laplace/native-streaming-delimited-recipe/v1",
                 "syntax": "Test/names",
                 "fields": [
                   {"syntaxPath": "record/@code", "propertyName": "code", "valueKind": "CodepointSequence",
                    "disposition": "Identity, Reference", "referenceCodec": "UnicodeCodepointSequence", "sequenceSeparator": " "},
                   {"syntaxPath": "record/@name", "propertyName": "name", "valueKind": "Text",
                    "disposition": "Content, Testimony", "relationName": "HAS_NAME"}],
                 "providerRoutes": [{"recordName": "record", "namespaceUri": "", "fieldPrefix": "record", "structurePaths": [],
                                     "subject": {"kind": "ContentField", "identityField": "code"}}],
                 "delimitedSyntax": {"recordName": "record", "separator": ";", "commentPrefix": "#", "trimFields": true,
                                     "columns": ["code", "name"]}}
                """);
            string manifest = Path.Combine(dir, "test.source.json");
            await File.WriteAllTextAsync(manifest, """
                {"authority": "Test/Zip", "release": "1", "sourceName": "ZipEntryTest", "trustClass": "StandardsDerived",
                 "layerOrder": 0, "selected": false, "root": "data",
                 "artifacts": [{"selector": "names.zip", "required": true, "disposition": "admitted",
                                "provider": "laplace/native-streaming-delimited-recipe/v1", "syntax": "Test/names",
                                "recipePath": "names.recipe.json", "recordDepth": 2,
                                "configuration": {"entries": ["a/first.txt", "a/second.txt"]}}]}
                """);
            string data = Path.Combine(dir, "data");
            Directory.CreateDirectory(data);
            using (var zip = ZipFile.Open(Path.Combine(data, "names.zip"), ZipArchiveMode.Create))
            {
                foreach ((string entry, string text) in new[] {
                             ("a/first.txt", "0041 0042;alpha beta\n"), ("a/second.txt", "0043 0044;gamma delta\n"),
                             ("a/ignored.txt", "0045 0046;epsilon zeta\n") })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                    await writer.WriteAsync(text);
                }
            }

            var recipe = SourceGenerationRecipe.Load(manifest);
            await using var decomposer = new Laplace.Decomposers.Structured.Decomposer<SourceGenerationRecipe>(recipe, data);
            var context = new FakeContext(new NullWriter()) { EcosystemPath = data };
            var rows = new List<AttestationRow>();
            await decomposer.InitializeAsync(context);
            await foreach (var change in decomposer.DecomposeAsync(context, DecomposerOptions.Default).WithoutWriter())
                foreach (IntentStage stage in change.IntentStages)
                    CopyTupleParser.DecodeAttestations([stage.TupleBuffer(IntentStageTable.Attestations)], rows);

            Hash128 name = RelationTypeRegistry.RelationTypeId("HAS_NAME");
            Hash128[] subjects = rows.Where(row => row.TypeId == name).Select(static row => row.SubjectId).ToArray();
            Assert.Equal(2, subjects.Length);
            Assert.Contains(ContentEmitter.RootId("AB")!.Value, subjects);
            Assert.Contains(ContentEmitter.RootId("CD")!.Value, subjects);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
