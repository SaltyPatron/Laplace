using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.SemLink;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Tests.Structured.SelectedRecipeSemanticsTests;

namespace Laplace.Decomposers.Tests.Structured;

/// <summary>
/// Same content, same id (Laplace-Wiki Storage/Identity.md): an identifier a source writes
/// (a VerbNet class "battle-36.4", a PropBank roleset "battle.01", a FrameNet lexical unit
/// "battle.n" in its frame) is content as written, so every source that names it reaches the
/// one id, and a word inside it is the one tier-2 word wherever it occurs. Per-span labels of
/// one annotated sentence are not claims about the span's text (Reference/Types.md: "belongs in
/// that sentence's annotation layer and never on the word"), and a count a source prints is not
/// a number of observations (Corpora/FrameNet.md: "witnessed once").
/// </summary>
public sealed class IdentifierIdentityLawTests
{
    private const string VerbNetClass = """
        <VNCLASS ID="battle-36.4">
          <MEMBERS>
            <MEMBER name="battle" wn="battle%2:33:00" grouping="battle.01" fn_mapping="Hostile_encounter" verbnet_key="battle#1"/>
          </MEMBERS>
          <THEMROLES/>
          <FRAMES/>
          <SUBCLASSES>
            <VNSUBCLASS ID="battle-36.4-1">
              <MEMBERS><MEMBER name="quarrel" wn="" grouping="quarrel.01" fn_mapping="None" verbnet_key="quarrel#1"/></MEMBERS>
              <THEMROLES/>
              <FRAMES/>
              <SUBCLASSES/>
            </VNSUBCLASS>
          </SUBCLASSES>
        </VNCLASS>
        """;

    private const string PropBankFrameset = """
        <frameset>
          <predicate lemma="battle">
            <roleset id="battle.01" name="fight">
              <roles>
                <role n="0" f="PAG" descr="fighter">
                  <rolelinks><rolelink class="battle-36.4" resource="VerbNet" version="verbnet3.4">agent</rolelink></rolelinks>
                </role>
              </roles>
              <lexlinks><lexlink class="battle-36.4" confidence="1.0" resource="VerbNet" version="verbnet3.4" src="manual"/></lexlinks>
              <example name="battle-v: transitive" src="">
                <text>They battled the storm.</text>
                <propbank>
                  <rel relloc="1">battled</rel>
                  <arg type="ARG0" start="0" end="0">They</arg>
                </propbank>
              </example>
            </roleset>
          </predicate>
        </frameset>
        """;

    private const string FrameNetLexUnit = """
        <lexUnit xmlns="http://framenet.icsi.berkeley.edu" ID="1" name="battle.n" frame="Hostile_encounter" POS="N" frameID="2" status="Finished_Initial" totalAnnotated="1">
          <lexeme POS="N" name="battle"/>
          <valences>
            <FERealization total="3">
              <FE name="Side_1"/>
              <pattern total="3"><valenceUnit GF="Gen" PT="Poss" FE="Side_1"/><annoSet ID="4"/></pattern>
            </FERealization>
          </valences>
          <subCorpus name="manually-added">
            <sentence ID="3" sentNo="0" aPos="0">
              <text>his battle ended</text>
              <annotationSet ID="4" status="MANUAL">
                <layer rank="1" name="FE"><label start="0" end="2" name="Side_1"/></layer>
                <layer rank="1" name="Target"><label start="4" end="9" name="Target"/></layer>
              </annotationSet>
            </sentence>
          </subCorpus>
        </lexUnit>
        """;

    private const string FrameNetFrame = """
        <frame xmlns="http://framenet.icsi.berkeley.edu" ID="2" name="Hostile_encounter">
          <lexUnit ID="1" name="battle.n" POS="N" status="Finished_Initial">
            <lexeme POS="N" name="battle"/>
            <sentenceCount annotated="547" total="600"/>
          </lexUnit>
        </frame>
        """;

    private static readonly Hash128 Word = Root("battle");

    [Fact]
    public void VerbNet_class_subclass_and_grouping_are_identifiers_as_written()
    {
        var rows = Read("recipes/verbnet/3.4/vn-class.recipe.json", VerbNetClass, recordDepth: 0);
        Hash128 cls = Root("battle-36.4"), sub = Root("battle-36.4-1");
        Hash128 member = Hash128.Merkle(EntityTier.Document, [cls, Word]);
        Assert.Contains(rows, r => Links(r, member, cls));
        Assert.Contains(rows, r => r.SubjectId == sub && r.ObjectId == cls);
        Assert.Contains(rows, r => Links(r, member, Root("battle.01")));
        // No decomposition of the identifier into [battle, 36, 4]: the separators are content.
        Hash128 split = Hash128.Merkle(EntityTier.Document, [Word, Root("36"), Root("4")]);
        Assert.DoesNotContain(rows, r => r.SubjectId == split || r.ObjectId == split);
    }

    [Fact]
    public void PropBank_roleset_and_its_VerbNet_links_are_identifiers_as_written()
    {
        var rows = Read("recipes/propbank/3.4/frameset.recipe.json", PropBankFrameset);
        Hash128 roleset = Root("battle.01"), cls = Root("battle-36.4");
        Assert.Contains(rows, r => r.SubjectId == roleset);
        Assert.Contains(rows, r => Links(r, roleset, cls));
        Assert.DoesNotContain(rows, r => r.SubjectId == Hash128.Merkle(EntityTier.Document, [Word, Root("01")]));
        // The example's argument and predicate spans are annotation of that example, never a
        // claim about the words "They" or "battled".
        Assert.DoesNotContain(rows, r => r.SubjectId == Root("They") || r.SubjectId == Root("battled"));
        // Only the roleset has roles; no span of an example does.
        Assert.DoesNotContain(rows, r => r.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SEMANTIC_ROLE")
            && r.SubjectId != roleset);
    }

    [Fact]
    public void FrameNet_lexical_unit_is_its_frame_and_name_from_every_reader()
    {
        Hash128 lu = Hash128.Merkle(EntityTier.Document, [Root("Hostile_encounter"), Root("battle.n")]);
        Assert.Equal(lu, FrameNetLexicalUnit.Id("Hostile_encounter", "battle.n"));

        var lexUnit = Read("recipes/framenet/1.7/lexunit.recipe.json", FrameNetLexUnit, recordDepth: 0);
        var valence = Assert.Single(lexUnit, r => r.TypeId == RelationTypeRegistry.RelationTypeId("HAS_VALENCE_PATTERN"));
        Assert.Equal(lu, valence.SubjectId);
        // pattern total="3" is a number FrameNet prints, not three observations.
        Assert.Equal(1, valence.ObservationCount);
        // Span labels of one annotated sentence say nothing of the span's text.
        Assert.DoesNotContain(lexUnit, r => r.SubjectId == Root("his"));
        Assert.DoesNotContain(lexUnit, r => r.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SEMANTIC_ROLE"));

        var frame = Read("recipes/framenet/1.7/frame.recipe.json", FrameNetFrame, recordDepth: 0);
        var evokes = Assert.Single(frame, r => r.TypeId == RelationTypeRegistry.RelationTypeId("EVOKES_FRAME"));
        Assert.Equal(lu, evokes.SubjectId);
        Assert.Equal(Root("Hostile_encounter"), evokes.ObjectId);
        // sentenceCount annotated="547" is a number FrameNet prints, not 547 observations.
        Assert.Equal(1, evokes.ObservationCount);
    }

    [Fact]
    public void Battle_roleset_and_class_reach_one_id_from_every_source()
    {
        var verbNet = Read("recipes/verbnet/3.4/vn-class.recipe.json", VerbNetClass, recordDepth: 0);
        var propBank = Read("recipes/propbank/3.4/frameset.recipe.json", PropBankFrameset);
        Hash128 roleset = AnchorAdmission.Id("battle.01", EntityTypeRegistry.PropBankRoleset)!.Value;
        Hash128 cls = AnchorAdmission.Id("battle-36.4", EntityTypeRegistry.VerbNetClass)!.Value;
        Assert.Equal(Root("battle.01"), roleset);
        Assert.Equal(Root("battle-36.4"), cls);
        Assert.Contains(verbNet, r => r.ObjectId == roleset || r.SubjectId == roleset);   // VerbNet's grouping
        Assert.Contains(propBank, r => r.SubjectId == roleset); // PropBank's own roleset
        Assert.Contains(verbNet, r => r.ObjectId == cls);       // VerbNet's own class
        Assert.Contains(propBank, r => Links(r, roleset, cls));      // PropBank's lexlink
    }

    [Fact]
    public void A_word_is_one_tier_two_id_alone_as_a_value_and_inside_text()
    {
        Hash128 word = Root("break");
        Assert.NotEqual(default, word);
        // The word alone is its codepoints' composition, not wrapped in a higher tier.
        Assert.Equal(word, Root("break"));
        Assert.NotEqual(word, Root("break."));
        Assert.NotEqual(word, Root("Break"));   // case is content, never folded
        // As a field value (a VerbNet member name) it is the same word.
        var rows = Read("recipes/verbnet/3.4/vn-class.recipe.json",
            VerbNetClass.Replace("name=\"battle\"", "name=\"break\""), recordDepth: 0);
        Assert.Contains(rows, r => r.SubjectId == Hash128.Merkle(EntityTier.Document, [Root("battle-36.4"), word]));
    }

    /// <summary>
    /// Generic gate over every recipe: no identity or object part splits a value on identifier
    /// punctuation (Storage/Ingestion.md: identifiers collapse as identifiers; Compositions.md:
    /// nothing is dropped), no testimony is said of a per-sentence span or value
    /// (Reference/Types.md), and no count field multiplies witnesses (Corpora/FrameNet.md:
    /// "witnessed once").
    /// </summary>
    [Fact]
    public void No_recipe_decomposes_identifiers_testifies_of_spans_or_inflates_witnesses()
    {
        string root = SourceGenerationRegressionTests.RepositoryFile("recipes");
        var recipes = Directory.EnumerateFiles(root, "*.recipe.json", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(recipes);
        var violations = new List<string>();
        foreach (string file in recipes)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Walk(doc.RootElement, Path.GetRelativePath(root, file), violations);
        }
        Assert.Empty(violations);
    }

    // CORRESPONDS_TO is symmetric: the substrate stores its endpoints in canonical id order.
    private static bool Links(AttestationRow r, Hash128 a, Hash128 b) =>
        (r.SubjectId == a && r.ObjectId == b) || (r.SubjectId == b && r.ObjectId == a);

    private static readonly char[] IdentifierPunctuation = ['.', '-', '_', ':', '%', '#', '/'];

    private static void Walk(JsonElement e, string where, List<string> violations)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray()) Walk(item, where, violations);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        if (e.TryGetProperty("splitLast", out var split)
            && split.GetString() is { } chars && chars.IndexOfAny(IdentifierPunctuation) >= 0)
            violations.Add($"{where}: splits '{Named(e)}' on identifier punctuation \"{chars}\"");
        foreach (string count in new[] { "observationPath", "observationField" })
            if (e.TryGetProperty(count, out _))
                violations.Add($"{where}: '{Named(e)}' reads witnesses from {count}");
        bool testimony = e.TryGetProperty("disposition", out var d)
            && d.GetString() is { } disposition && disposition.Contains("Testimony");
        if (testimony && e.TryGetProperty("subjectMode", out var mode) && mode.GetString() == "Span")
            violations.Add($"{where}: '{Named(e)}' testifies of a span");
        if (testimony && e.TryGetProperty("contextField", out var context) && context.GetString() == "text()")
            violations.Add($"{where}: '{Named(e)}' testifies of a per-sentence value");
        foreach (var p in e.EnumerateObject()) Walk(p.Value, where, violations);
    }

    private static string Named(JsonElement e) =>
        e.TryGetProperty("syntaxPath", out var s) ? s.GetString()!
        : e.TryGetProperty("path", out var p) ? p.GetString()!
        : e.TryGetProperty("childPath", out var c) ? c.GetString()!
        : e.TryGetProperty("recordName", out var r) ? r.GetString()! : "?";
}
