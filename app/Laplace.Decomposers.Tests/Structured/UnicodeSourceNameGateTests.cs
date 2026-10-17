using System.Text.Json;
using System.Text.RegularExpressions;
using Laplace.Decomposers.Unicode;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

/// <summary>
/// The Unicode source is named by its recipe (recipes/unicode/UCD/17.0.0/unicode.source.json),
/// not by a C# class (#1877). Layer completion, the journal, the CLI status probe, the gates and
/// the seed scripts all key on that one name; a second spelling would split layer 0 in two.
/// </summary>
public sealed class UnicodeSourceNameGateTests
{
    private const string RecipeName = "Unicode";

    [Fact]
    public void Unicode_recipe_and_source_share_one_name()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            SourceGenerationRegressionTests.RepositoryFile("recipes/unicode/UCD/17.0.0/unicode.source.json")));
        Assert.Equal(RecipeName, doc.RootElement.GetProperty("sourceName").GetString());
        Assert.Equal(RecipeName, UnicodeSource.SourceName);
        Assert.Equal(Laplace.Engine.Core.SubstrateCanonicalIds.Source(RecipeName), UnicodeSource.SourceId);
    }

    // A source-name use of the old spelling: a quoted literal, a source_id() argument, a CLI
    // -Src value, a layer_ok argument or a seed list token. Class and file references
    // (UnicodeDecomposer.cs, new UnicodeDecomposer()) are not source names.
    private static readonly Regex OldSourceName = new(
        @"[""']UnicodeDecomposer[""']|source/UnicodeDecomposer/|-Src UnicodeDecomposer|layer_ok unicode UnicodeDecomposer|for %%S in \(UnicodeDecomposer |STEP_SOURCE=UnicodeDecomposer",
        RegexOptions.CultureInvariant);

    [Fact]
    public void No_reference_names_the_unicode_source_by_its_old_class_name()
    {
        string root = SourceGenerationRegressionTests.RepositoryFile(".");
        string[] areas = ["app", "scripts", "extension", "recipes", "engine/manifest", ".github"];
        string[] exts = [".cs", ".json", ".sh", ".cmd", ".ps1", ".py", ".sql", ".in", ".yml", ".toml"];
        var offenders = new List<string>();
        foreach (string area in areas)
        {
            string dir = Path.Combine(root, area);
            if (!Directory.Exists(dir)) continue;
            foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!exts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)
                    || rel.Contains("node_modules", StringComparison.Ordinal)
                    || rel.EndsWith("UnicodeSourceNameGateTests.cs", StringComparison.Ordinal)
                    || rel.EndsWith("Hash128Tests.cs", StringComparison.Ordinal))
                    continue;
                string text = File.ReadAllText(path);
                if (OldSourceName.IsMatch(text)) offenders.Add(rel);
            }
        }
        Assert.True(offenders.Count == 0,
            "The Unicode source is named 'Unicode' (its recipe); old name still used in:\n  " + string.Join("\n  ", offenders));
    }
}