using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Prompt-topic election is one operation. It now has one BODY — converse.elect — and
/// the callers that used to copy its key order delegate to it instead.
///
/// This gate used to assert that five separate ORDER BY clauses matched each other. That
/// is a check this file's own history shows is not enough: it can prove five copies are
/// IDENTICAL but never that they are RIGHT, and W4 recorded the consequence — "the gate
/// is honest and the thing it guards is hollow". Spec 37's implementation law is the
/// standard being enforced here instead: "There is one canonical implementation per
/// operation fact… Endpoint-specific helpers delegate to the same program."
///
/// So the assertions are now asymmetric by design:
///   - the canonical body carries the key order,
///   - the delegates carry NO election of their own (a copy reappearing is a failure),
///   - the language-filtered electors, which rank a genuinely different candidate set,
///     still key-match the canonical order,
///   - explicitly declared per-constituent consumers may read every coherence row, but
///     may not collapse those rows through converse.elect or regrow the election order.
/// </summary>
public sealed class ElectorArchitectureGateTests
{
    /// <summary>
    /// The one body. Every projection off the election lives here.
    /// </summary>
    private const string CanonicalElector =
        "extension/laplace_substrate/sql/functions/converse/elect.sql.in";

    /// <summary>
    /// These rank a LANGUAGE-FILTERED candidate set — a different program over the same
    /// coherence rows, so they are not collapsible into converse.elect without changing
    /// what they decide. They remain key-pinned to the canonical order: same judgement,
    /// different candidates.
    /// </summary>
    private static readonly string[] LanguageFilteredElectors =
    [
        "extension/laplace_substrate/sql/functions/converse/infer.sql.in",
        "extension/laplace_substrate/sql/functions/converse/orient_topic.sql.in",
    ];

    /// <summary>
    /// These consume the PER-CONSTITUENT result of prompt_coherence rather than electing
    /// one prompt topic. prompt_coherence already returns the best witnessed sense for
    /// each prompt token; preserving all of those rows is a different operation from
    /// converse.elect's OP6/OP7 prompt collapse. The forward pass is intentionally here:
    /// replacing it with converse.elect would discard prompt constituents before ROUTE.
    /// </summary>
    private static readonly string[] PerConstituentCoherenceConsumers =
    [
        "extension/laplace_substrate/sql/functions/generation/walk_text.sql.in",
    ];

    /// <summary>
    /// Sites that USED to carry a copy of the election and now delegate. Listed so that a
    /// re-inlined ORDER BY is a test failure rather than a silent regression to six bodies.
    /// </summary>
    private static readonly string[] DelegatingSites =
    [
        "extension/laplace_substrate/sql/functions/converse/converse.sql.in",
        "extension/laplace_substrate/sql/functions/converse/converse_walk.sql.in",
        "extension/laplace_substrate/sql/functions/converse/resolve_topic.sql.in",
    ];

    private static IEnumerable<string> KeyPinnedElectors
        => LanguageFilteredElectors.Prepend(CanonicalElector);

    /// <summary>
    /// Every signal is a separate ordered dimension. Combining specificity and folded
    /// evidence in a product made an implicit weighting policy out of unrelated units;
    /// keeping them explicit makes the election stable as the seed grows.
    ///
    /// This list changed on 2026-08-11 and the change is why this gate exists. The
    /// weighted order landed in chat.sql.in ALONE, leaving the other four sites on the
    /// old keys — precisely the drift this file was written to catch, and it caught it.
    /// Collapsing the copies into converse.elect is what removes the drift surface.
    /// </summary>
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

    private static readonly Regex ElectCall = new(
        @"(?:(?:@extschema@|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?"
        + @"\belect(?:_topic|_sense)?\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void PromptElectors_UseOneCanonicalKeyOrder()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();

        foreach (var relativePath in KeyPinnedElectors)
        {
            var path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Declared elector does not exist: {relativePath}");

            var orders = ExtractElectorOrders(File.ReadAllText(path));
            Assert.True(orders.Count == 1,
                $"{relativePath} must contain exactly one prompt election ORDER BY; found {orders.Count}.");
            Assert.Equal(ExpectedElectorKeys, orders[0]);
        }
    }

    /// <summary>
    /// The collapse itself. A delegate that regrows an ORDER BY on the election keys has
    /// re-forked the operation, which is the exact drift the six-copy arrangement caused.
    /// </summary>
    [Fact]
    public void DelegatingSites_CarryNoElectionOfTheirOwn()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();

        foreach (var relativePath in DelegatingSites)
        {
            var path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Declared delegate does not exist: {relativePath}");

            var sql = File.ReadAllText(path);
            var orders = ExtractElectorOrders(sql);
            Assert.True(orders.Count == 0,
                $"{relativePath} delegates to converse.elect and must not rank the election "
                + $"itself; found {orders.Count} election ORDER BY clause(s).");

            Assert.True(ElectCall.IsMatch(StripComments(sql)),
                $"{relativePath} is declared a delegate but does not call converse.elect*.");
        }
    }

    /// <summary>
    /// A per-constituent consumer is not an elector exemption. It is a separately pinned
    /// operation shape: it must consume prompt_coherence directly, preserve that row set,
    /// and carry neither the single-topic elect call nor a local copy of its key order.
    /// </summary>
    [Fact]
    public void PerConstituentConsumers_DoNotCollapseToOnePromptTopic()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();

        foreach (var relativePath in PerConstituentCoherenceConsumers)
        {
            var path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Declared per-constituent consumer does not exist: {relativePath}");

            var sql = StripComments(File.ReadAllText(path));
            Assert.True(PromptCoherenceCall.IsMatch(sql),
                $"{relativePath} no longer consumes per-constituent prompt_coherence rows.");
            Assert.False(ElectCall.IsMatch(sql),
                $"{relativePath} must preserve the per-constituent frontier, not collapse through converse.elect*.");
            Assert.Empty(ExtractElectorOrders(sql));
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
        var declared = KeyPinnedElectors
            .Concat(PerConstituentCoherenceConsumers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = callers.Except(declared).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var stale = declared.Except(callers).Order(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(unknown.Count == 0,
            "New prompt_coherence callers must delegate to converse.elect when they elect one topic, "
            + "be declared/key-pinned when they rank a different candidate set, or be declared as a "
            + "non-electing per-constituent consumer:\n  "
            + string.Join("\n  ", unknown));
        Assert.True(stale.Count == 0,
            "Declared prompt_coherence consumers no longer call it; remove or rewire them:\n  "
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

    [Fact]
    public void PromptCoherence_HasNoSeedScaleOrCrossUnitScoreProduct()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var source = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "src", "prompt_coherence.c"));

        Assert.DoesNotContain("1.0e13", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mass_sat", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pc_load_icf", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("structural.entity_container_degree(", source,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is_namer ? -1.0 : share", source, StringComparison.Ordinal);

        foreach (var relativePath in KeyPinnedElectors.Concat(DelegatingSites))
        {
            var sql = StripComments(File.ReadAllText(Path.Combine(
                repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.DoesNotMatch(@"specificity\s*[^,\r\n]*\*", sql);
        }
    }

    [Fact]
    public void PromptCoherence_SpecificityUsesBidirectionalIncidentMass()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var source = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "src", "prompt_coherence.c"));

        Assert.Contains("cands[me->idx[i]].total_mass += rank * eff;", source,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"if\s*\(forward\)\s*for\s*\([^)]*\)\s*cands\[me->idx\[i\]\]\.total_mass",
            StripComments(source));
    }

    private static List<IReadOnlyList<string>> ExtractElectorOrders(string sql)
    {
        var text = StripComments(sql);
        var orders = new List<IReadOnlyList<string>>();

        foreach (Match orderBy in OrderBy.Matches(text))
        {
            var parsed = TryParseElectorKeySequence(text, orderBy.Index + orderBy.Length);
            if (parsed is not null)
                orders.Add(parsed);
        }

        return orders;
    }

    private static IReadOnlyList<string>? TryParseElectorKeySequence(string text, int index)
    {
        var keys = new List<string>();
        var cursor = index;

        while (true)
        {
            var key = ElectorKey.Match(text, cursor);
            if (!key.Success)
                return keys.Count == 0 ? null : keys;

            var name = key.Groups["name"].Value.ToUpperInvariant();
            var direction = key.Groups["direction"].Success ? " DESC" : string.Empty;
            var nulls = key.Groups["nulls"].Success ? " NULLS LAST" : string.Empty;
            keys.Add(name + direction + nulls);
            cursor = key.Index + key.Length;

            var comma = Comma.Match(text, cursor);
            if (!comma.Success)
                return keys;
            cursor = comma.Index + comma.Length;
        }
    }

    private static string StripComments(string sql)
        => SqlComments.Replace(sql, string.Empty);
}
