using System.Text;
using Laplace.Decomposers.OMW;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class GrammarSpineConformanceTests
{
    [Fact]
    public void OmwRowParser_ParsesLemmaRow()
    {
        ReadOnlySpan<byte> line = "1740-n\teng:lemma\tcat"u8;
        Assert.True(OMWRowParser.TryParseRow(line, "eng", out var row, out var valueUtf8));
        Assert.Equal(OmwType.Lemma, row.Type);
        Assert.Equal("cat", Encoding.UTF8.GetString(valueUtf8));
        Assert.Equal("eng", row.Lang);
    }

    [Fact]
    public void OmwRowParser_SkipsCommentLines()
    {
        Assert.False(OMWRowParser.TryParseRow("# comment"u8, "eng", out _, out _));
    }

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
    /// Comments are stripped before the needle search. Without that, a needle naming a base
    /// class is satisfied by any file that MENTIONS it — which is what happened: after
    /// WiktionaryDecomposer moved to ComposeDecomposer (PR #944), the "GrammarIngestDecomposer"
    /// needle kept passing on the strength of one doc-comment in WiktionaryGrammarWitness.cs,
    /// so the gate reported a spine the code had already left. A conformance test that a
    /// sentence can satisfy measures prose, not structure.
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
    public void TabularDecomposers_UseStructuredGrammarIngest()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var grammarSpine = new (string Project, string[] Needles)[]
        {
            // Wiktionary left the GrammarIngestDecomposer base in PR #944 for the native
            // per-row parse; the compose lane, not the tree-sitter row spine, is what it
            // actually runs. The needle now names the base it declares.
            ("Wiktionary", ["ComposeDecomposer<WiktionaryEntry", "WiktionaryEmit.Emit"]),
            ("SemLink", ["GrammarIngestHandler", "SemLinkGrammarWitness", "IGrammarWitness"]),
            // Tatoeba is two PHASES (sentences then links) rather than parallel files —
            // the link phase needs the id -> content-root map the sentence phase produces.
            // Still the grammar spine, reached through DecomposerPhase<GrammarIngestRecord>.
            ("Tatoeba", ["DecomposerPhase<GrammarIngestRecord", "GrammarIngestHandler",
                "TatoebaGrammarWitness", "IngestPipelineDefaults.StructuredGrammar"]),
            // ConceptNet: monolith triple — ExtractFileAsync unit on RelationTripleDecomposerBase.
            ("ConceptNet", ["RelationTripleRecord", "ExtractFileAsync", "RelationTripleDecomposerBase"]),
            ("OMW", ["DecomposerMultiFile<GrammarIngestRecord", "ExtractFileAsync",
                "GrammarIngestHandler", "OMWGrammarWitness"]),
            // Atomic: multi-file triple — same ExtractFileAsync unit via RelationTripleMultiFile.
            ("Atomic2020", ["RelationTripleRecord", "ExtractFileAsync", "RelationTripleMultiFileDecomposerBase"]),
            ("UD", ["DecomposerMultiFile<UdIngestRecord", "ExtractFileAsync",
                "UdIngestHandler", "UdConlluParser"]),
        };

        foreach (var (project, needles) in grammarSpine)
        {
            var dir = Path.Combine(repoRoot, "app", "Laplace.Decomposers", project);
            var text = string.Join('\n', Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(p => StripComments(File.ReadAllText(p))));
            foreach (var needle in needles)
                Assert.True(text.Contains(needle, StringComparison.Ordinal),
                    $"{project} must use grammar spine pattern '{needle}' in CODE "
                    + "(comments are stripped — a mention is not a use)");
        }
    }

    [Fact]
    public void OpenSubtitles_UsesIngestPipeline_NotHandRolledBuilderLoop()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var dir = Path.Combine(repoRoot, "app", "Laplace.Decomposers", "OpenSubtitles");
        var text = string.Join('\n', Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains("bin") && !p.Contains("obj"))
            .Select(p => StripComments(File.ReadAllText(p))));
        Assert.Contains("OpenSubtitlesZipIngest", text, StringComparison.Ordinal);
        Assert.Contains("DecomposerMultiFile<AlignedSubtitleBlock", text, StringComparison.Ordinal);
        Assert.Contains("OpenSubtitlesAlignedHandler", text, StringComparison.Ordinal);
        Assert.Contains("ExtractFileAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RelationTripleMultiFileDecomposerBase", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new SubstrateChangeBuilder", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenSubtitlesIngestHandler", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenSubtitlesFastIngest", text, StringComparison.Ordinal);
    }
}
