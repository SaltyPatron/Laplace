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
        "extension/laplace_substrate/sql/functions/converse/chat.sql.in",
        "extension/laplace_substrate/sql/functions/converse/converse.sql.in",
        "extension/laplace_substrate/sql/functions/converse/converse_walk.sql.in",
        "extension/laplace_substrate/sql/functions/converse/infer.sql.in",
        "extension/laplace_substrate/sql/functions/converse/resolve_topic.sql.in",
    ];

    private static readonly string[] ExpectedElectorKeys =
    [
        "SPECIFICITY DESC NULLS LAST",
        "REL_MASS DESC NULLS LAST",
        "PEERS DESC",
        "ORD DESC",
        "DENOTE_MU DESC NULLS LAST",
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
