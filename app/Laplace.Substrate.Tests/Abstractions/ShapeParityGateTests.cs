using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Query-shape parity: every place that names the recall shape vocabulary agrees with the
/// <c>converse.query_shapes()</c> catalog, and a drifting site fails by name. One of the
/// sites is prose in an MCP tool description, so agreement is checked rather than generated.
///
/// <para><b>The declarations:</b></para>
/// <list type="number">
///   <item><c>converse/query_shapes.sql.in</c> — the catalog: shapes with
///     <c>needs_topic2</c> / <c>needs_type</c> / <c>accepts_lang</c>; the source for
///     everything below.</item>
///   <item><c>src/recall_route.c</c> <c>route_intents[]</c> — the C membership test
///     behind <c>route_intent_known()</c>; same shapes, same order.</item>
///   <item><c>src/recall.c</c> <c>kSingleArgIntents[]</c> — the uniform single-argument
///     responders. A SUBSET, so it is gated as a subset, not as equality.</item>
///   <item><c>src/recall.c</c> two <c>errhint</c>s — they point at
///     <c>converse.query_shapes()</c> rather than list the vocabulary.</item>
///   <item><c>Laplace.Endpoints.Mcp/SubstrateTools.cs</c> — the client menu, as English
///     prose. Both the shape list AND the three requirement clauses are derived from the
///     catalog's boolean columns and checked against it.</item>
/// </list>
///
/// <para><c>converse/chat.sql.in</c> branches on shape name literals and is gated as a
/// subset, like kSingleArgIntents.</para>
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
    /// The MCP menu, anchored on "names the SHAPE"; a missing anchor is itself a failure.
    /// </summary>
    private static readonly Regex McpShapeMenu = new(
        // The menu may cite the catalog as converse.query_shapes() or laplace.query_shapes();
        // either way its shape list is checked.
        @"names the SHAPE\s*[—-]\s*(?<list>[a-z_,\s]+?)\s*\(SELECT \* FROM (?:laplace|converse)\.query_shapes\(\)",
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
    /// Declaration 2: same shapes in the same order as the catalog.
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
    /// Declaration 5, part two — the requirement clauses in the menu prose match the
    /// catalog's <c>needs_topic2</c> / <c>needs_type</c> / <c>accepts_lang</c> columns.
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
    /// Declaration 3 — a subset: a single-argument responder for a shape the catalog does
    /// not publish is unreachable through <c>recall_intent</c>, which rejects unknown shapes.
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
    /// Declaration 4 — the two <c>recall_intent</c> rejections refer callers to the catalog
    /// instead of enumerating the vocabulary.
    /// </summary>
    [Fact]
    public void ShapeParity_UnknownShapeErrorsPointAtTheCatalog()
    {
        var recall = Read(RecallPath);
        const string hint = "errhint(\"SELECT shape FROM converse.query_shapes()\")";
        Assert.Equal(2, Regex.Matches(recall, Regex.Escape(hint)).Count);
    }

    /// <summary>
    /// The default intent is a published shape: <c>converse.recall()</c> with no explicit
    /// shape routes through <c>ROUTE_DEFAULT_INTENT</c>.
    /// </summary>
    [Fact]
    public void ShapeParity_DefaultIntentIsAPublishedShape()
    {
        var m = Regex.Match(Read(RecallPath), @"#define\s+ROUTE_DEFAULT_INTENT\s+""(?<name>[a-z_]+)""");
        Assert.True(m.Success, $"ROUTE_DEFAULT_INTENT is no longer declared in {RecallPath}.");
        Assert.Contains(m.Groups["name"].Value, Catalog().Select(s => s.Name));
    }

    /// <summary>
    /// <c>converse.chat()</c> branches only on catalog shapes. Subset: chat special-cases some
    /// shapes and delegates the rest to <c>recall_intent</c>.
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
