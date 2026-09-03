using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Identity/realization law (INVENTIONS #58, GH #901/#1404).
///
/// Human presentation is selected from substrate structure/evidence at the read edge.
/// It may not infer semantic kind or language by parsing an already-rendered string,
/// and clients may not invent a second normalization policy.  This is deliberately a
/// source gate: it makes the architectural boundary fail before a live database or UI
/// can hide a regression behind plausible-looking text.
/// </summary>
public sealed class UniversalRealizationContractTests
{
    private static string Root => TypeIdLawTests.FindRepoRootPublic();

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void UniversalRealization_DoesNotParseRenderedStrings()
    {
        var scalar = Read("extension", "laplace_substrate", "sql", "functions", "converse", "label.sql.in");
        var batch = Read("extension", "laplace_substrate", "sql", "functions", "converse", "label_batch.sql.in");
        var canonical = Read("extension", "laplace_substrate", "sql", "functions", "realize", "realize_canonical.sql.in");
        var native = Read("extension", "laplace_substrate", "src", "realize_batch.c");

        foreach (var text in new[] { scalar, batch, canonical })
        {
            Assert.DoesNotContain("regexp_replace", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("language:([", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tiny-codes/concept", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source/file/", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("WHERE n.id = p_id", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("name LIKE 'substrate/%'", canonical, StringComparison.OrdinalIgnoreCase);

        // Scalar and native batch canonical lookup are one policy.  The native query
        // must return the exact registry value, not strip a path-shaped prefix.
        Assert.Contains("SELECT n.id, n.name", native, StringComparison.Ordinal);
        Assert.DoesNotContain("regexp_replace(n.name", native, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("n.name LIKE 'substrate/%'", native, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductDisplay_CarriesStructureLanguageAndFallbackExplicitly()
    {
        var batch = Read("extension", "laplace_substrate", "sql", "functions", "converse", "label_batch.sql.in");

        Assert.Contains("CREATE OR REPLACE FUNCTION realize.display_batch", batch, StringComparison.Ordinal);
        Assert.Contains("language_id", batch, StringComparison.Ordinal);
        Assert.Contains("type_id", batch, StringComparison.Ordinal);
        Assert.Contains("technical_name", batch, StringComparison.Ordinal);
        Assert.Contains("type_name", batch, StringComparison.Ordinal);
        Assert.Contains("realization text", batch, StringComparison.Ordinal);
        Assert.Contains("'content'", batch, StringComparison.Ordinal);
        Assert.Contains("'name'", batch, StringComparison.Ordinal);
        Assert.Contains("'canonical'", batch, StringComparison.Ordinal);
        Assert.Contains("'id:' || left(encode(f.id, 'hex'), 12)", batch, StringComparison.Ordinal);

        // Tier is reported as a structural coordinate only when unambiguous; it is
        // never used as a semantic-kind switch.
        Assert.Contains("count(DISTINCT e.tier) = 1", batch, StringComparison.Ordinal);
        Assert.DoesNotContain("CASE f.tier", batch, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WHEN f.tier", batch, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Leaderboards_UseOneRealizerAndKeepGlickoCoordinatesDistinct()
    {
        var sql = Read("extension", "laplace_substrate", "sql", "functions", "ops", "band_leaders.sql.in");
        var web = Read("web", "src", "home", "Leaderboards.tsx");

        Assert.Contains("realize.display_batch", sql, StringComparison.Ordinal);
        Assert.Contains("consensus.rating_display", sql, StringComparison.Ordinal);
        Assert.Contains("consensus.rd_display", sql, StringComparison.Ordinal);
        Assert.Contains("consensus.eff_mu_display", sql, StringComparison.Ordinal);
        Assert.Contains("subject_realization", sql, StringComparison.Ordinal);
        Assert.Contains("object_realization", sql, StringComparison.Ordinal);

        Assert.DoesNotContain(".replace(", web, StringComparison.Ordinal);
        Assert.DoesNotContain(".toLowerCase(", web, StringComparison.Ordinal);
        Assert.Contains("subject_realization", web, StringComparison.Ordinal);
        Assert.Contains("object_realization", web, StringComparison.Ordinal);
        Assert.Contains("row.rating", web, StringComparison.Ordinal);
        Assert.Contains("row.rd", web, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatScaffold_HasNoHardcodedLanguageTable_AndDefaultRemainsForwardPass()
    {
        var scaffold = Read("extension", "laplace_substrate", "sql", "functions", "converse", "chat_scaffold.sql.in");
        var chat = Read("extension", "laplace_substrate", "sql", "functions", "converse", "chat.sql.in");

        Assert.DoesNotContain("language:eng", scaffold, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("language:bul", scaffold, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("has parts such as", scaffold, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Kinds of", scaffold, StringComparison.Ordinal);
        Assert.DoesNotContain("is related to", scaffold, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("ELSIF shape IS NULL THEN", chat, StringComparison.Ordinal);
        Assert.Contains("generation.forward_text(", chat, StringComparison.Ordinal);
        Assert.Contains("p_prompt, 40, 5, 0.6, 10", chat, StringComparison.Ordinal);
    }
}
