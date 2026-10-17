using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

/// <summary>
/// Recipe law gates (Laplace-Engine recipes/BRIEF.md, Laplace-Wiki Corpora/README.md):
/// a WordNet sense key is an internal pointer to a lexicalization, decomposed for its facts,
/// resolved through the highway and recorded nowhere; and a record a recipe reads is a record,
/// shown as its parts, never minted as a text Document. These fail if any checked-in recipe
/// records a sense key, if the sense-key entity type comes back, or if the native recipe stream
/// composes a row or a file's content tree as a text type again.
/// </summary>
public sealed class RecipeLawGateTests
{
    private static readonly Regex SenseKeyField = new(
        @"(^|/)@(sensekey|wn)$|^Sense/@dc:identifier$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IEnumerable<string> RecipeFiles() =>
        Directory.EnumerateFiles(SourceGenerationRegressionTests.RepositoryFile("recipes"), "*.json",
                SearchOption.AllDirectories)
            .OrderBy(static p => p, StringComparer.Ordinal);

    [Fact]
    public void No_recipe_records_a_sense_key()
    {
        var offenders = new List<string>();
        foreach (string path in RecipeFiles())
        {
            string text = File.ReadAllText(path);
            if (text.Contains("WordNet_Sense_Key", StringComparison.Ordinal))
                offenders.Add($"{path}: mints a WordNet_Sense_Key entity");
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("fields", out var fields)
                || fields.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var field in fields.EnumerateArray())
            {
                string syntaxPath = field.TryGetProperty("syntaxPath", out var sp) ? sp.GetString() ?? "" : "";
                string property = field.TryGetProperty("propertyName", out var pn) ? pn.GetString() ?? "" : "";
                bool senseKey = SenseKeyField.IsMatch(syntaxPath)
                                || property.Equals("senseKey", StringComparison.OrdinalIgnoreCase)
                                || property.Equals("wordNetSense", StringComparison.OrdinalIgnoreCase);
                if (!senseKey) continue;
                string disposition = field.TryGetProperty("disposition", out var d) ? d.GetString() ?? "" : "";
                bool records = disposition.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(static x => x is not ("Packaging" or "Excluded"));
                if (records || field.TryGetProperty("relationName", out _) || field.TryGetProperty("objectParts", out _))
                    offenders.Add($"{path}: {syntaxPath} ({property}) is recorded as '{disposition}'");
            }
        }
        Assert.True(offenders.Count == 0,
            "A sense key is a pointer, resolved and recorded nowhere (BRIEF.md):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Sense_key_entity_type_is_not_governed()
    {
        string types = File.ReadAllText(SourceGenerationRegressionTests.RepositoryFile("engine/manifest/entity_types.toml"));
        Assert.DoesNotContain("\"WordNet_Sense_Key\"", types, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipe_records_are_records_not_text_documents()
    {
        string types = File.ReadAllText(SourceGenerationRegressionTests.RepositoryFile("engine/manifest/entity_types.toml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var record = Regex.Match(types, "\\[\\[type\\]\\]\\ncanonical = \"Source_Record\"\\n(?<rest>(?:[^\\[\\n][^\\n]*\\n)*)");
        Assert.True(record.Success, "Source_Record must be a governed type");
        Assert.DoesNotContain("text = true", record.Groups["rest"].Value, StringComparison.Ordinal);

        string stream = File.ReadAllText(SourceGenerationRegressionTests.RepositoryFile("engine/core/src/recipe_stream.cpp"));
        var trunkTypes = Regex.Matches(stream, "compose_trunk\\([^;]*?\"(?<type>[A-Za-z_]+)\"\\)")
            .Select(static m => m.Groups["type"].Value).Distinct().OrderBy(static t => t, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "SourceFile", "Source_Record" }, trunkTypes);
        Assert.DoesNotContain("\"Document\"", stream, StringComparison.Ordinal);
    }
}
