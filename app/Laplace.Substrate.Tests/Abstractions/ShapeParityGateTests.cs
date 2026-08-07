using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// ISA gate G5 — shape parity
/// (<c>docs/specs/37_Substrate_Operation_ISA.md</c> §7: <i>"converse.query_shapes(), the C dispatch,
/// and the client menu are not all generated from the §3 table"</i>; plan
/// <c>docs/plan/W6_Architecture_Gates.md</c> §3/§8.3).
///
/// <para><b>Pin agreement, not generation.</b> W6 §8.3: the fifth declaration is PROSE in
/// an MCP tool description, and <i>"generating that string is a code change, not a gate."</i>
/// So this gate asserts the five hand-written declarations agree, and fails by name when
/// one drifts — which is what the elector invariant (#771) proved is the failure mode
/// worth catching, a set growing a site while its prose stayed behind.</para>
///
/// <para><b>The five declarations</b>, measured 2026-08-05, all currently in agreement —
/// <b>zero violations</b>, no allowlist:</para>
/// <list type="number">
///   <item><c>converse/query_shapes.sql.in</c> — the catalog: 14 shapes with
///     <c>needs_topic2</c> / <c>needs_type</c> / <c>accepts_lang</c>. THE SOURCE for
///     everything below.</item>
///   <item><c>src/recall_route.c</c> <c>route_intents[]</c> — the C membership test
///     behind <c>route_intent_known()</c>. Same 14, same order.</item>
///   <item><c>src/recall.c</c> <c>kSingleArgIntents[]</c> — the uniform single-argument
///     responders. A SUBSET, so it is gated as a subset, not as equality.</item>
///   <item><c>src/recall.c</c> two <c>errhint</c>s — the only correct way for C to name
///     the vocabulary: point at <c>converse.query_shapes()</c> rather than list it a third
///     time.</item>
///   <item><c>Laplace.Endpoints.Mcp/SubstrateTools.cs</c> — the client menu, as English
///     prose. Both the shape list AND the three requirement clauses are derived from the
///     catalog's boolean columns and checked against it.</item>
/// </list>
///
/// <para><c>converse/chat.sql.in</c> is a sixth site in practice — it branches on shape
/// name literals — and is gated as a subset for the same reason as kSingleArgIntents.</para>
/// </summary>
public sealed class ShapeParityGateTests
{
    private const string QueryShapesPath =
        "extension/laplace_substrate/sql/functions/converse/query_shapes.sql.in";
    private const string RecallRoutePath = "extension/laplace_substrate/src/recall_route.c";
    private const string RecallPath = "extension/laplace_substrate/src/recall.c";
    private const string ChatPath =
        "extension/laplace_substrate/sql/functions/converse/chat.sql.in";
    private const string McpToolsPath = "app/Laplace.Endpoints.Mcp/SubstrateTools.cs";

    /// <summary>
    /// The catalog is a <c>VALUES</c> list of
    /// <c>(shape, summary, needs_topic2, needs_type, accepts_lang)</c>.
    /// </summary>
    private static readonly Regex CatalogRow = new(
        @"\(\s*'(?<shape>[a-z_]+)'\s*,\s*'(?:[^']|'')*'\s*,\s*"
        + @"(?<topic2>true|false)\s*,\s*(?<type>true|false)\s*,\s*(?<lang>true|false)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RouteIntentsBlock = new(
        @"route_intents\[\]\s*=\s*\{(?<body>[\s\S]*?)\};", RegexOptions.Compiled);

    private static readonly Regex SingleArgBlock = new(
        @"kSingleArgIntents\[\]\s*=\s*\{(?<body>[\s\S]*?)\n\};", RegexOptions.Compiled);

    private static readonly Regex QuotedName = new(@"""(?<name>[a-z_]+)""", RegexOptions.Compiled);

    private static readonly Regex SingleArgEntry = new(
        @"\{\s*""(?<name>[a-z_]+)""\s*,", RegexOptions.Compiled);

    /// <summary>
    /// The MCP menu. Anchored on "names the SHAPE" so a reworded sentence around it does
    /// not silently stop being checked — the anchor missing is itself a failure.
    /// </summary>
    private static readonly Regex McpShapeMenu = new(
        @"names the SHAPE\s*[—-]\s*(?<list>[a-z_,\s]+?)\s*\(SELECT \* FROM laplace\.query_shapes\(\)",
        RegexOptions.Compiled);

    private static readonly Regex McpNeedsType = new(
        @"(?<shapes>[a-z_/]+) need relation_type", RegexOptions.Compiled);
    private static readonly Regex McpNeedsTopic2 = new(
        @"(?<shapes>[a-z_/]+) need topic2", RegexOptions.Compiled);
    private static readonly Regex McpAcceptsLang = new(
        @"(?<shapes>[a-z_/]+) accepts lang", RegexOptions.Compiled);

    /// <summary>Shape-name literals in <c>converse.chat()</c>'s branch conditions.</summary>
    private static readonly Regex ChatShapeLiteral = new(
        @"\bshape\s+(?:NOT\s+)?IN\s*\((?<list>[^)]*)\)|\bshape\s*=\s*'(?<one>[a-z_]+)'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string Read(string relative)
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var path = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"G5 declaration site is missing: {relative}");
        return File.ReadAllText(path);
    }

    private sealed record Shape(string Name, bool NeedsTopic2, bool NeedsType, bool AcceptsLang);

    /// <summary>Declaration 1 — the catalog, in declaration order.</summary>
    private static List<Shape> Catalog()
    {
        var rows = CatalogRow.Matches(
                RenderBeforeSelectGateTests.StripSqlComments(Read(QueryShapesPath)))
            .Select(m => new Shape(
                m.Groups["shape"].Value,
                bool.Parse(m.Groups["topic2"].Value),
                bool.Parse(m.Groups["type"].Value),
                bool.Parse(m.Groups["lang"].Value)))
            .ToList();
        Assert.True(rows.Count > 0,
            $"{QueryShapesPath} parsed to zero shapes — the VALUES layout changed and this "
            + "gate is measuring nothing. Fix the parse before trusting a green.");
        return rows;
    }

    private static List<string> BlockNames(Regex block, Regex entry, string text, string what)
    {
        var m = block.Match(text);
        Assert.True(m.Success, $"G5 could not find {what}; the gate is measuring nothing.");
        var names = entry.Matches(m.Groups["body"].Value)
            .Select(x => x.Groups["name"].Value).ToList();
        Assert.True(names.Count > 0, $"{what} parsed to zero entries.");
        return names;
    }

    /// <summary>
    /// Declaration 2. Order matters as much as membership: the two lists are read side by
    /// side by anyone adding a shape, and an equal-set-but-shuffled pair is exactly the
    /// state in which a reviewer stops diffing them.
    /// </summary>
    [Fact]
    public void ShapeParity_CDispatchMatchesCatalog_InOrder()
    {
        var expected = Catalog().Select(s => s.Name).ToList();
        var actual = BlockNames(RouteIntentsBlock, QuotedName, Read(RecallRoutePath),
            $"route_intents[] in {RecallRoutePath}");
        Assert.Equal(expected, actual);
    }

    /// <summary>Declaration 5, part one — the client menu names the same shapes, in order.</summary>
    [Fact]
    public void ShapeParity_McpMenuMatchesCatalog_InOrder()
    {
        var expected = Catalog().Select(s => s.Name).ToList();
        var m = McpShapeMenu.Match(Read(McpToolsPath));
        Assert.True(m.Success,
            $"{McpToolsPath} no longer publishes a shape menu in the form this gate reads "
            + "(\"names the SHAPE — <list> (SELECT * FROM converse.query_shapes()\"). Either "
            + "restore it or re-anchor the gate — do not leave the menu unpinned.");
        var actual = m.Groups["list"].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Declaration 5, part two — the requirement clauses. These are the part of the prose
    /// most likely to rot: adding a shape that needs <c>topic2</c> changes a boolean column
    /// in SQL and an English clause in C#, and nothing connects them but this test.
    /// </summary>
    [Fact]
    public void ShapeParity_McpRequirementProseMatchesCatalogFlags()
    {
        var catalog = Catalog();
        var tools = Read(McpToolsPath);

        static SortedSet<string> Prose(Regex rule, string text, string clause)
        {
            var m = rule.Match(text);
            Assert.True(m.Success, $"the MCP query tool no longer states \"{clause}\".");
            return new SortedSet<string>(m.Groups["shapes"].Value.Split('/', StringSplitOptions.TrimEntries));
        }

        static SortedSet<string> Flagged(List<Shape> catalog, Func<Shape, bool> flag) =>
            new(catalog.Where(flag).Select(s => s.Name));

        Assert.Equal(Flagged(catalog, s => s.NeedsType), Prose(McpNeedsType, tools, "… need relation_type"));
        Assert.Equal(Flagged(catalog, s => s.NeedsTopic2), Prose(McpNeedsTopic2, tools, "… need topic2"));
        Assert.Equal(Flagged(catalog, s => s.AcceptsLang), Prose(McpAcceptsLang, tools, "… accepts lang"));
    }

    /// <summary>
    /// Declaration 3 — a subset by design (one table replacing N copy-pasted if-arms), so
    /// the fact is containment: a single-arg responder for a shape the catalog does not
    /// publish is unreachable through <c>recall_intent</c>, which rejects unknown shapes.
    /// </summary>
    [Fact]
    public void ShapeParity_SingleArgRespondersAreCatalogShapes()
    {
        var catalog = Catalog().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var singleArg = BlockNames(SingleArgBlock, SingleArgEntry, Read(RecallPath),
            $"kSingleArgIntents[] in {RecallPath}");
        var unknown = singleArg.Where(s => !catalog.Contains(s)).ToList();
        Assert.True(unknown.Count == 0,
            "kSingleArgIntents names shapes converse.query_shapes() does not publish, so recall_intent "
            + "rejects them before the responder is ever reached:\n  " + string.Join("\n  ", unknown));
    }

    /// <summary>
    /// Declaration 4 — the two <c>recall_intent</c> rejections. The gate's real content is
    /// that C refers callers to the catalog instead of enumerating the vocabulary a third
    /// time; a hint that listed the shapes inline would be a sixth declaration.
    /// </summary>
    [Fact]
    public void ShapeParity_UnknownShapeErrorsPointAtTheCatalog()
    {
        var recall = Read(RecallPath);
        const string hint = "errhint(\"SELECT shape FROM converse.query_shapes()\")";
        Assert.Equal(2, Regex.Matches(recall, Regex.Escape(hint)).Count);
    }

    /// <summary>
    /// The default intent must be a published shape. <c>converse.recall()</c> with no explicit shape
    /// routes through <c>ROUTE_DEFAULT_INTENT</c>, so a default that fell out of the catalog
    /// would break the bare-prompt path and nothing else would say so.
    /// </summary>
    [Fact]
    public void ShapeParity_DefaultIntentIsAPublishedShape()
    {
        var m = Regex.Match(Read(RecallPath), @"#define\s+ROUTE_DEFAULT_INTENT\s+""(?<name>[a-z_]+)""");
        Assert.True(m.Success, $"ROUTE_DEFAULT_INTENT is no longer declared in {RecallPath}.");
        Assert.Contains(m.Groups["name"].Value, Catalog().Select(s => s.Name));
    }

    /// <summary>
    /// The sixth site W6 does not count: <c>converse.chat()</c> branches on shape name literals.
    /// Subset, because chat deliberately special-cases only some shapes and delegates the
    /// rest to <c>recall_intent</c>.
    /// </summary>
    [Fact]
    public void ShapeParity_ChatBranchesOnCatalogShapesOnly()
    {
        var catalog = Catalog().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var chat = RenderBeforeSelectGateTests.StripSqlComments(Read(ChatPath));

        var literals = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in ChatShapeLiteral.Matches(chat))
        {
            if (m.Groups["one"].Success) literals.Add(m.Groups["one"].Value);
            foreach (Match q in Regex.Matches(m.Groups["list"].Value, @"'(?<name>[a-z_]+)'"))
                literals.Add(q.Groups["name"].Value);
        }

        Assert.True(literals.Count > 0,
            $"no shape literals found in {ChatPath} — converse.chat() stopped branching on shape "
            + "names, or the parse broke. Either way this fact is measuring nothing.");
        var unknown = literals.Where(s => !catalog.Contains(s)).ToList();
        Assert.True(unknown.Count == 0,
            "converse.chat() branches on shape names converse.query_shapes() does not publish:\n  "
            + string.Join("\n  ", unknown));
    }
}
