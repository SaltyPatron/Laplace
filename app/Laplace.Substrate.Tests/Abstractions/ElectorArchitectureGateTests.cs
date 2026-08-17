using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Prompt-topic election is one operation with several SQL callers. Every caller must
/// use the same ordered keys, and every new caller must join this declaration rather
/// than silently copying and drifting from it.
/// </summary>
public sealed class ElectorArchitectureGateTests
{
    private static readonly string[] ElectorSites =
    [
        "extension/laplace_substrate/sql/functions/converse/converse.sql.in",
        "extension/laplace_substrate/sql/functions/converse/converse_walk.sql.in",
        "extension/laplace_substrate/sql/functions/converse/infer.sql.in",
        "extension/laplace_substrate/sql/functions/converse/orient_topic.sql.in",
        "extension/laplace_substrate/sql/functions/converse/resolve_topic.sql.in",
    ];

    /// <summary>
    /// GH #865. The primary key is the PRODUCT of coherence and fold-witness, not
    /// coherence alone: specificity is coherence over the candidate's OWN total mass,
    /// so leading with it makes rarity a virtue, and denote_mu previously sat at key
    /// five behind three keys that never tie on real data — never consulted.
    ///
    /// denote_mu is deliberately GONE from the tail. It is inside the product now;
    /// leaving it in both places would let it break ties it had already won or lost.
    ///
    /// This list changed on 2026-08-11 and the change is why this gate exists. The
    /// weighted order landed in chat.sql.in ALONE, leaving the other four sites on the
    /// old keys — precisely the drift this file was written to catch, and it caught it
    /// (`found 0`, because the leading key is now an expression rather than a bare
    /// column). All five sites move together or the gate fails.
    /// </summary>
    private static readonly string[] ExpectedElectorKeys =
    [
        "SPECIFICITY*DENOTE_MU DESC",
        "SPECIFICITY DESC NULLS LAST",
        "REL_MASS DESC NULLS LAST",
        "PEERS DESC",
        "ORD DESC",
        "SYNSET_ID",
    ];

    private static readonly HashSet<string> ElectorExemptions =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex SqlComments = new(
        @"/\*[\s\S]*?\*/|--[^\r\n]*",
        RegexOptions.Compiled);

    private static readonly Regex OrderBy = new(
        @"\bORDER\s+BY\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The weighted primary key, matched as one unit and normalised to
    /// "SPECIFICITY*DENOTE_MU DESC". Written across lines in every site, so the
    /// pattern must tolerate arbitrary whitespace between the operands.
    /// </summary>
    private static readonly Regex WeightedElectorKey = new(
        @"\G\s*\(\s*COALESCE\s*\(\s*(?:(?:@extschema@\.)?y\.)?specificity\s*,\s*0\s*\)\s*"
        + @"\*\s*GREATEST\s*\(\s*COALESCE\s*\(\s*(?:(?:@extschema@\.)?y\.)?denote_mu\s*,\s*0\s*\)\s*,\s*1\s*\)\s*\)"
        + @"(?<direction>\s+DESC\b)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ElectorKey = new(
        @"\G\s*(?:(?:@extschema@\.)?y\.)?"
        + @"(?<name>specificity|rel_mass|peers|ord|denote_mu|synset_id)\b"
        + @"(?<direction>\s+DESC\b)?"
        + @"(?<nulls>\s+NULLS\s+LAST\b)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Comma = new(
        @"\G\s*,",
        RegexOptions.Compiled);

    private static readonly Regex PromptCoherenceCall = new(
        @"(?:(?:@extschema@|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?"
        + @"\bprompt_coherence\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void PromptElectors_UseOneCanonicalKeyOrder()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();

        foreach (var relativePath in ElectorSites)
        {
            var path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Declared elector does not exist: {relativePath}");

            var orders = ExtractElectorOrders(File.ReadAllText(path));
            Assert.True(orders.Count == 1,
                $"{relativePath} must contain exactly one prompt election ORDER BY; found {orders.Count}.");
            Assert.Equal(ExpectedElectorKeys, orders[0]);
        }
    }

    [Fact]
    public void PromptElectorDeclarations_MatchAllSqlCallers()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var functionsRoot = Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "functions");
        var definitionPath = Path.Combine("converse", "prompt_coherence.sql.in")
            .Replace('\\', '/');

        var callers = Directory.EnumerateFiles(functionsRoot, "*.sql.in", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(functionsRoot, path).Replace('\\', '/');
                return !relative.Equals(definitionPath, StringComparison.OrdinalIgnoreCase)
                    && PromptCoherenceCall.IsMatch(StripComments(File.ReadAllText(path)));
            })
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declared = ElectorSites.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = callers.Except(declared).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var stale = declared.Except(callers).Order(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(unknown.Count == 0,
            "New prompt_coherence callers must be declared and key-pinned:\n  "
            + string.Join("\n  ", unknown));
        Assert.True(stale.Count == 0,
            "Declared electors no longer call prompt_coherence; remove or rewire them:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void PromptElectorExemptions_AreEmpty()
        => Assert.Empty(ElectorExemptions);

    [Fact]
    public void PromptCoherence_DefineVerb_IsPinnedToDefinitionRelation()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var source = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "src", "prompt_coherence.c"));

        Assert.Matches(@"\{\s*""definition""\s*,\s*""define""\s*\}", source);
    }

    private static List<IReadOnlyList<string>> ExtractElectorOrders(string sql)
    {
        var text = StripComments(sql);
        var orders = new List<IReadOnlyList<string>>();

        foreach (Match orderBy in OrderBy.Matches(text))
        {
            var keys = ReadOrderKeys(text, orderBy.Index + orderBy.Length);
            if (keys.Any(key => key.StartsWith("SPECIFICITY", StringComparison.Ordinal)))
                orders.Add(keys);
        }

        return orders;
    }

    private static IReadOnlyList<string> ReadOrderKeys(string text, int position)
    {
        var keys = new List<string>();
        while (position < text.Length)
        {
            // The weighted product is tried FIRST: it opens with '(' where a bare column
            // name would be, so ElectorKey cannot match it and the whole clause would
            // read as "not an election" — which is exactly how a one-site change slipped
            // past as `found 0` instead of failing as drift.
            var weighted = WeightedElectorKey.Match(text, position);
            if (weighted.Success && weighted.Index == position)
            {
                var weightedKey = "SPECIFICITY*DENOTE_MU";
                if (weighted.Groups["direction"].Success)
                    weightedKey += " DESC";
                keys.Add(weightedKey);
                position = weighted.Index + weighted.Length;

                var weightedComma = Comma.Match(text, position);
                if (!weightedComma.Success || weightedComma.Index != position)
                    break;
                position = weightedComma.Index + weightedComma.Length;
                continue;
            }

            var key = ElectorKey.Match(text, position);
            if (!key.Success || key.Index != position)
            {
                if (keys.Count > 0)
                    keys.Add("<UNPARSED>");
                break;
            }

            var normalized = key.Groups["name"].Value.ToUpperInvariant();
            if (key.Groups["direction"].Success)
                normalized += " DESC";
            if (key.Groups["nulls"].Success)
                normalized += " NULLS LAST";
            keys.Add(normalized);
            position = key.Index + key.Length;

            var comma = Comma.Match(text, position);
            if (!comma.Success || comma.Index != position)
                break;
            position = comma.Index + comma.Length;
        }

        return keys;
    }

    private static string StripComments(string sql)
        => SqlComments.Replace(sql, string.Empty);
}
