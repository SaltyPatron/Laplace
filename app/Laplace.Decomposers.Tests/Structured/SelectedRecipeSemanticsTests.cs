using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class SelectedRecipeSemanticsTests
{
    [Theory]
    [InlineData("recipes/oewn/2025-plus/wn-lmf-1.4.recipe.json", 1)]
    [InlineData("recipes/oewn/2025-plus/wn-lmf-1.4.recipe.json", 4096)]
    [InlineData("recipes/omw/2.0/wn-lmf-1.4.recipe.json", 7)]
    public void Wordnet_sense_examples_and_forward_references_keep_their_concept(string recipe, int chunkSize)
    {
        const string source = """
            <LexicalResource><Lexicon id="test" label="Test" language="en" version="1">
              <LexicalEntry id="entry-bank"><Lemma writtenForm="bank" partOfSpeech="n"/>
                <Sense id="sense-finance" synset="syn-finance">
                  <Example>the bank lent money</Example>
                  <SenseRelation target="sense-river" relType="antonym"/>
                </Sense>
                <Sense id="sense-river" synset="syn-river"><Example>the river bank</Example></Sense>
              </LexicalEntry>
              <Synset id="syn-finance" ili="i100" partOfSpeech="n"><Definition>a financial institution</Definition></Synset>
              <Synset id="syn-river" ili="i200" partOfSpeech="n"><Definition>land beside water</Definition></Synset>
            </Lexicon></LexicalResource>
            """;
        var rows = Read(recipe, source, chunkSize);
        Hash128 finance = Hash128.Merkle(EntityTier.Document, [Root("bank"), Root("i100")]);
        Hash128 river = Hash128.Merkle(EntityTier.Document, [Root("bank"), Root("i200")]);
        Hash128 example = RelationTypeRegistry.RelationTypeId("HAS_EXAMPLE");
        Assert.Contains(rows, r => r.SubjectId == finance && r.TypeId == example && r.ObjectId == Root("the bank lent money"));
        Assert.Contains(rows, r => r.SubjectId == river && r.TypeId == example && r.ObjectId == Root("the river bank"));
        Assert.DoesNotContain(rows, r => r.SubjectId == Root("bank") && r.TypeId == example);
        // IS_ANTONYM_OF is symmetric, so the substrate stores its endpoints in canonical id order
        // (RelationTypeRegistryTests.Symmetric_EndpointOrderCanonicalized_OneRow), not source order.
        Assert.Contains(rows, r => r.TypeId == RelationTypeRegistry.RelationTypeId("IS_ANTONYM_OF")
            && ((r.SubjectId == finance && r.ObjectId == river) || (r.SubjectId == river && r.ObjectId == finance)));
        Assert.Contains(rows, r => r.SubjectId == Root("bank") && r.ObjectId == finance
            && r.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SENSE") && r.ContextId == Root("eng"));
        // Packaging names may change without changing the denoted sense or any testimony.
        var renamed = Read(recipe, source.Replace("sense-", "renamed-sense-").Replace("syn-", "renamed-syn-"), chunkSize);
        Assert.Equal(rows.Select(r => r with { LastObservedAtUnixUs = 0 }),
            renamed.Select(r => r with { LastObservedAtUnixUs = 0 }));
    }

    [Fact]
    public void Wordnet_dangling_sense_reference_is_rejected_instead_of_becoming_content()
    {
        var error = Assert.Throws<InvalidDataException>(() => Read("recipes/oewn/2025-plus/wn-lmf-1.4.recipe.json", """
            <LexicalResource><Lexicon id="test" language="en" version="1">
              <LexicalEntry id="entry"><Lemma writtenForm="bank" partOfSpeech="n"/>
                <Sense id="sense" synset="absent"/>
              </LexicalEntry>
            </Lexicon></LexicalResource>
            """));
        Assert.Contains("unresolved concept reference", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void Cili_recipe_preserves_type_definition_language_and_original_attribution(int chunkSize)
    {
        var rows = Read("recipes/cili/a895d7e/ili.recipe.json", """
            @prefix ili: <http://globalwordnet.org/cili/> .
            @prefix skos: <http://www.w3.org/2004/02/skos/core#> .
            @prefix dc: <http://purl.org/dc/elements/1.1/> .
            @prefix pwn30: <http://wordnet-rdf.princeton.edu/wn30/> .
            ili:i1 a ili:Concept ; skos:definition "a concept"@en ; dc:source pwn30:02084071-n .
            ili:i2 a ili:Instance ; skos:definition "an instance"@en .
            """, chunkSize);
        AssertClaim(rows, "i1", "IS_TYPED_AS", "Concept");
        AssertClaim(rows, "i2", "IS_TYPED_AS", "Instance");
        AssertClaim(rows, "i2", "HAS_DEFINITION", "an instance", "eng");
        Assert.DoesNotContain(rows, r => r.SubjectId == Root("i1")
            && r.TypeId == RelationTypeRegistry.RelationTypeId("HAS_DEFINITION"));
        AssertClaim(rows, "i1", "HAS_ATTRIBUTION", "http://wordnet-rdf.princeton.edu/wn30/");
        Assert.DoesNotContain(rows, r => r.SubjectId == Root("i2")
            && r.TypeId == RelationTypeRegistry.RelationTypeId("HAS_ATTRIBUTION"));
    }

    [Fact]
    public void Attributed_cili_definition_remains_content_without_an_independent_vote()
    {
        const string source = """
            @prefix ili: <http://globalwordnet.org/cili/> .
            @prefix skos: <http://www.w3.org/2004/02/skos/core#> .
            @prefix dc: <http://purl.org/dc/elements/1.1/> .
            @prefix pwn30: <http://wordnet-rdf.princeton.edu/wn30/> .
            ili:i1 a ili:Concept ; skos:definition "a concept"@en ; dc:source pwn30:02084071-n .
            """;
        Hash128 first = default, second = default;
        Read("recipes/cili/a895d7e/ili.recipe.json", source, 4096, file => first = file.Id);
        Read("recipes/cili/a895d7e/ili.recipe.json", source.Replace("a concept", "a different definition"), 4096, file => second = file.Id);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Iso_recipe_preserves_comment_as_a_scoped_source_statement()
    {
        var rows = Read("recipes/iso639/20260415/code-table.recipe.json",
            "Id\tPart2b\tPart2t\tPart1\tScope\tLanguage_Type\tRef_Name\tComment\n"
            + "eng\teng\teng\ten\tI\tL\tEnglish\tSource qualification\n", 7);
        AssertClaim(rows, "eng", "HAS_LANGUAGE_SCOPE", "Individual");
        AssertClaim(rows, "eng", "HAS_LANGUAGE_TYPE", "Living");
        AssertClaim(rows, "eng", "HAS_ATTRIBUTE", "Source qualification", "Comment");
    }

    [Fact]
    public void File_content_retains_repeated_records_and_their_order()
    {
        const string recipe = "recipes/iso639/20260415/code-table.recipe.json";
        const string header = "Id\tPart2b\tPart2t\tPart1\tScope\tLanguage_Type\tRef_Name\tComment\n";
        const string english = "eng\teng\teng\ten\tI\tL\tEnglish\t\n";
        const string french = "fra\tfre\tfra\tfr\tI\tL\tFrench\t\n";
        Hash128 RootFor(string records, int chunk = 4096)
        {
            Hash128 root = default;
            Read(recipe, header + records, chunk, file => root = file.Id);
            Assert.NotEqual(default, root);
            return root;
        }
        Assert.NotEqual(RootFor(english), RootFor(english + english));
        Assert.NotEqual(RootFor(english + french), RootFor(english + french + english));
        Assert.NotEqual(RootFor(english + french + english), RootFor(english + english + french));
        Assert.Equal(RootFor(english + french + english), RootFor(english + french + english, 1));
    }

    [Fact]
    public void File_content_retains_repeated_examples_inside_one_sense()
    {
        const string recipe = "recipes/oewn/2025-plus/wn-lmf-1.4.recipe.json";
        const string example = "<Example>the river bank</Example>";
        const string source = """
            <LexicalResource><Lexicon id="test" language="en" version="1">
              <LexicalEntry id="entry"><Lemma writtenForm="bank" partOfSpeech="n"/>
                <Sense id="sense" synset="synset">EXAMPLES</Sense>
              </LexicalEntry>
              <Synset id="synset" ili="i200" partOfSpeech="n"/>
            </Lexicon></LexicalResource>
            """;
        Hash128 once = default, twice = default;
        Read(recipe, source.Replace("EXAMPLES", example), 4096, file => once = file.Id);
        Read(recipe, source.Replace("EXAMPLES", example + example), 4096, file => twice = file.Id);
        Assert.NotEqual(once, twice);
    }

    internal static List<AttestationRow> Read(string recipePath, string input, int chunkSize = 4096,
        Action<OrderedCompositionComponent>? fileCompleted = null)
    {
        CodepointPerfcache.LoadDefault();
        var recipe = new LaplaceCookbook().InstallJson(SourceGenerationRegressionTests.RepositoryFile(recipePath));
        using var stream = NativeRecipeStream.Open(NativeRecipeCompiler.Compile(recipe),
            SourceWitness.Id(recipe.Authority, recipe.Release), 1);
        if (fileCompleted is not null)
        {
            using var metadata = new SubstrateChangeBuilder(default, "test metadata");
            stream.SetFile(ContentEmitter.StageComponent(metadata, "source metadata", default)!.Value);
        }
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        if (stream.RequiresPrescan) stream.Prescan(bytes, true);
        var rows = new List<AttestationRow>();
        for (int offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            int length = Math.Min(chunkSize, bytes.Length - offset);
            stream.Feed(bytes.AsSpan(offset, length), offset + length == bytes.Length);
            while (true)
            {
                using var stage = stream.Drain(100_000, 64 * 1024 * 1024, out _);
                if (stage is null) break;
                CopyTupleParser.DecodeAttestations([stage.TupleBuffer(IntentStageTable.Attestations)], rows);
            }
        }
        if (fileCompleted is not null) fileCompleted(stream.FileRoot!.Value);
        return rows;
    }

    internal static Hash128 Root(string value) => ContentEmitter.RootId(value)!.Value;

    internal static void AssertClaim(IEnumerable<AttestationRow> rows, string subject,
        string relation, string value, string? context = null) =>
        Assert.Contains(rows, r => r.SubjectId == Root(subject)
            && r.TypeId == RelationTypeRegistry.RelationTypeId(relation)
            && r.ObjectId == Root(value) && (context is null || r.ContextId == Root(context)));
}
