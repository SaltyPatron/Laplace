using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// ISA gate G2 — render-before-select
/// (<c>docs/specs/37_Substrate_Operation_ISA.md</c> §7, plan
/// <c>docs/plan/W6_Architecture_Gates.md</c> §3/§5 D4).
///
/// <para><b>The law.</b> CLAUDE.md, Reads: <i>"Never render an entity to text in order
/// to classify it. Classification is an indexed read on the id; the render is the
/// cost."</i> and <i>"Don't resolve names per row. Aggregate ids, then batch through
/// realize_batch."</i> A SCALAR realizer in a row-producing SELECT is one text
/// materialization per row; the sanctioned surface is the set-returning
/// <c>realize.batch()</c> / <c>render_text_batch()</c>, which resolve a whole id array
/// in one pass.</para>
///
/// <para><b>Why this is a ratchet and not a ban.</b> W6 D4: G2 is <i>not</i> decidable by
/// a naive regex. Three families false-positive and are excluded by construction, not by
/// allowlist entry:</para>
/// <list type="bullet">
///   <item><c>realize/</c> — the realizer bodies themselves, plus
///     <c>realize_batch.sql.in</c>, which IS the sanctioned surface.</item>
///   <item><c>readback/</c> — <c>render()</c> / <c>render_text*()</c> are defined here;
///     a definition is not a per-row call.</item>
///   <item><c>lexical/type_label.sql.in</c> and <c>converse/label*.sql.in</c> — the label
///     bodies. <c>converse.label_is_content()</c> in particular takes ALREADY-RENDERED text and
///     renders nothing, so every regex that names it is wrong about it.</item>
/// </list>
///
/// <para>What is left after those exclusions is the hand-drawn violator list below,
/// enumerated and dated. It MAY ONLY SHRINK. A new <c>.sql.in</c> that renders per row
/// fails by name; migrating one to <c>realize_batch</c> forces deleting its entry
/// (<see cref="RenderBeforeSelect_Allowlist_HasNoStaleEntries"/>).</para>
///
/// <para>Not every entry is a defect awaiting a fix. Several render a bounded set — one
/// gloss per answered question (<c>lexical/define*.sql.in</c>), one row per arena or
/// source (<c>ops/*_counts*.sql.in</c>). Those are cheap and may sit here permanently.
/// The gate's job is that the NEXT one is a decision, not an accident.</para>
/// </summary>
public sealed class RenderBeforeSelectGateTests
{
    /// <summary>
    /// A SCALAR realizer call. <c>realize_batch</c> and <c>render_text_batch</c> do not
    /// match: <c>_</c> is a word character, so <c>\brealize\s*\(</c> cannot reach the
    /// <c>(</c> of <c>realize.batch(</c>. <c>@extschema@.</c> qualification is optional
    /// because the tree writes both forms.
    /// </summary>
    private static readonly Regex ScalarRealizer = new(
        @"\b(?:@extschema@\.)?(realize|realize_canonical|render|render_text|render_text_fast"
        + @"|type_label|label|label_or_hex)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] ExcludedDirectories = ["realize/", "readback/"];
    private static readonly string[] ExcludedPrefixes = ["converse/label"];
    private static readonly string[] ExcludedFiles = ["lexical/type_label.sql.in"];

    /// <summary>
    /// The sanctioned surface. Asserted to exist so that excluding <c>realize/</c> can
    /// never quietly become an exclusion of nothing.
    /// </summary>
    private const string BatchSurface = "realize/realize_batch.sql.in";

    /// <summary>
    /// Files calling a scalar realizer per row, measured 2026-08-05 (54 files, 111 call
    /// sites). THIS LIST MAY ONLY SHRINK. The fix for an entry is to aggregate the ids
    /// and join <c>realize.batch()</c> once.
    ///
    /// <para>W6 §5 D4 estimated "~30 files" for this list. That estimate was not measured:
    /// the comment-stripped count over the same exclusions is 54 files / 111 sites.</para>
    ///
    /// <para>Reading the annotations: <c>Nx</c> is the number of scalar-realizer calls in
    /// that file after comment strip, followed by the function names and the line numbers
    /// as of the measurement date. Line numbers drift; the file name is the key.</para>
    /// </summary>
    private static readonly HashSet<string> ScalarRealizerAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- converse: the speaking loop renders as it composes. The largest cluster and
        // --- the one with the most to gain from batching.
        "converse/chat.sql.in",                        // 4x realize @ 249,267,272,277
        "converse/converse_about.sql.in",              // 1x realize @ 31
        "converse/converse_compose.sql.in",            // 4x realize/render_text @ 137,183,189,189
        "converse/converse_facts.sql.in",              // 10x label/realize/render_text/type_label @ 40,40,45,100,100,121,154,172,173,173
        "converse/converse_tiered.sql.in",             // 2x realize/render_text @ 245,245
        "converse/converse_walk.sql.in",               // 1x render_text @ 201
        "converse/correlate.sql.in",                   // 2x label @ 19,20
        "converse/epistemic_status.sql.in",            // 2x realize/type_label @ 23,24
        "converse/links.sql.in",                       // 2x label @ 49,53
        "converse/realize_path_with_dirs.sql.in",      // 4x realize/type_label @ 7,9,10,11

        // --- recall: the responder family. Each response renders the reply it returns.
        "recall/recall_examples_response.sql.in",      // 1x label @ 5
        "recall/recall_fallback_gloss.sql.in",         // 1x label @ 8
        "recall/recall_interaction_response.sql.in",   // 2x label/render_text @ 51,51
        "recall/recall_is_a_no_reply.sql.in",          // 4x label/realize @ 6,6,8,8
        "recall/recall_related_response.sql.in",       // 1x label @ 5
        "recall/recall_walk_response.sql.in",          // 10x label/realize/type_label @ 46,46,47,48,49,57,57,58,59,60
        "recall/recall_what_is_response.sql.in",       // 1x label @ 8

        // --- ops: bounded row counts (one row per arena / source / entity type). Cheap
        // --- by construction; these may legitimately stay.
        "ops/arena_counts.sql.in",                     // 1x render @ 6
        "ops/band_leaders.sql.in",                     // 2x label_or_hex @ 9,11
        "ops/entity_type_counts.sql.in",               // 1x render @ 5
        "ops/entity_type_counts_approx.sql.in",        // 1x render @ 43
        "structural/mesh_position.sql.in",             // 6x label_or_hex @ 10,11,15,21,23,36
        "ops/source_counts.sql.in",                    // 1x render @ 8
        "ops/source_counts_approx.sql.in",             // 1x render @ 26
        "ops/source_status.sql.in",                    // 1x render @ 90
        "taxonomy/taxonomy_tree.sql.in",               // 3x label_or_hex @ 10,12,17

        // --- lexical: one gloss per answered question. Bounded; likely permanent.
        "lexical/define.sql.in",                       // 1x render_text @ 5
        "lexical/define_with_context.sql.in",          // 1x render_text @ 5
        "lexical/examples.sql.in",                     // 1x render_text @ 5

        // --- inspect: operator-facing readable views.
        "inspect/consensus_out_labeled.sql.in",        // 1x type_label @ 38
        "inspect/consensus_out_readable.sql.in",       // 2x render @ 8,8
        "inspect/consensus_partition_pressure.sql.in", // 1x label @ 49
        "inspect/evidence_receipt.sql.in",             // 2x render/type_label @ 64,132
        "inspect/top_relations_readable.sql.in",       // 3x render @ 5,5,5

        // --- generation / corpus: vocabulary materialization renders every vocab id.
        // --- The highest-cardinality entries in this list.
        "corpus/corpus_whitespace_vocab_indices.sql.in", // 1x render_text_fast @ 56
        "generation/corpus_word_vocab.sql.in",         // 1x render_text @ 21
        "generation/grapheme_floor_vocab.sql.in",      // 1x render_text @ 28
        "generation/recall_trajectories.sql.in",       // 1x render_text @ 16
        "generation/walk_text.sql.in",                 // 2x render_text @ 19,19

        // --- structural / geometry / taxonomy / link / model / chess / consensus.
        "chess/chess_game.sql.in",                     // 1x render_text @ 92
        "consensus/edges.sql.in",                      // 2x label @ 59,67
        "consensus/relate_path.sql.in",                // 1x type_label @ 83
        "consensus/salient_facts.sql.in",              // 1x type_label @ 77
        "geometry/structural_cluster.sql.in",          // 1x render_text @ 43
        "link/concept_peers.sql.in",                   // 1x label @ 59
        "model/model_factor.sql.in",                   // 1x render_text @ 169
        "structural/anagrams_of.sql.in",               // 1x render @ 5
        "structural/collocates.sql.in",                // 1x render @ 13
        "structural/explore_anchor_neighbors.sql.in",  // 4x label_or_hex/render_text_fast @ 66,67,96,97
        "taxonomy/retrieve_grounded.sql.in",           // 2x render_text @ 36,46
        "taxonomy/synset_gloss.sql.in",                // 1x render_text @ 5
    };

    /// <summary>
    /// Ratchet ceilings, measured 2026-08-05. Lower as files migrate to
    /// <c>realize_batch</c>; never raise. Compile-time consts on purpose (W6 D2): a
    /// ceiling in generated data is a ceiling nobody reviews.
    /// </summary>
    private const int ScalarRealizerFileCeiling = 51;

    /// <inheritdoc cref="ScalarRealizerFileCeiling"/>
    /// <remarks>
    /// The second dimension. Without it a file could triple its per-row renders and stay
    /// green because its NAME is still one entry.
    /// </remarks>
    private const int ScalarRealizerSiteCeiling = 111;

    private static string FunctionsRoot(string repoRoot) =>
        Path.Combine(repoRoot, "extension", "laplace_substrate", "sql", "functions");

    private static bool IsExcluded(string relative) =>
        ExcludedDirectories.Any(d => relative.StartsWith(d, StringComparison.OrdinalIgnoreCase))
        || ExcludedPrefixes.Any(p => relative.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || ExcludedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Strip <c>--</c> and <c>/* */</c> while preserving quoted literals and newlines.
    /// Mirrors <c>scripts/isa-gate-check.py:strip_sql_comments</c> — the two gates must
    /// agree on what "in the code" means or their counts drift apart.
    /// </summary>
    internal static string StripSqlComments(string text)
    {
        var outBuf = new StringBuilder(text.Length);
        var state = 0; // 0 code, 1 line comment, 2 block comment, 3 string
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char n = i + 1 < text.Length ? text[i + 1] : '\0';
            switch (state)
            {
                case 0:
                    if (c == '-' && n == '-') { outBuf.Append("  "); i++; state = 1; continue; }
                    if (c == '/' && n == '*') { outBuf.Append("  "); i++; state = 2; continue; }
                    if (c == '\'') state = 3;
                    outBuf.Append(c);
                    break;
                case 1:
                    if (c is '\r' or '\n') { outBuf.Append(c); state = 0; }
                    else outBuf.Append(' ');
                    break;
                case 2:
                    if (c == '*' && n == '/') { outBuf.Append("  "); i++; state = 0; continue; }
                    outBuf.Append(c is '\r' or '\n' ? c : ' ');
                    break;
                default:
                    outBuf.Append(c);
                    if (c == '\'' && n == '\'') { outBuf.Append(n); i++; continue; }
                    if (c == '\'') state = 0;
                    break;
            }
        }
        return outBuf.ToString();
    }

    /// <summary>Relative path -> scalar-realizer call count, sorted, exclusions applied.</summary>
    private static SortedDictionary<string, int> Violators()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var root = FunctionsRoot(repoRoot);
        var found = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(root), $"substrate function tree not found at {root}");

        foreach (var file in Directory.EnumerateFiles(root, "*.sql.in", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsExcluded(relative)) continue;
            var count = ScalarRealizer.Matches(StripSqlComments(File.ReadAllText(file))).Count;
            if (count > 0) found[relative] = count;
        }
        return found;
    }

    /// <summary>
    /// The exclusions are load-bearing. If <c>realize_batch</c> ever moves, excluding
    /// <c>realize/</c> stops meaning "the sanctioned surface" and starts meaning
    /// "wherever the batch call happens to be", which is how a gate rots quietly.
    /// </summary>
    [Fact]
    public void RenderBeforeSelect_BatchSurfaceExists()
    {
        var path = Path.Combine(FunctionsRoot(TypeIdLawTests.FindRepoRootPublic()),
            BatchSurface.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path),
            $"{BatchSurface} is the surface G2 points offenders at, and the reason "
            + "realize/ is excluded wholesale. It is missing — re-derive this gate's "
            + "exclusions before trusting its count.");
    }

    [Fact]
    public void RenderBeforeSelect_NoNewScalarRealizerCallers()
    {
        var newcomers = Violators().Keys.Where(v => !ScalarRealizerAllowlist.Contains(v)).ToList();
        Assert.True(newcomers.Count == 0,
            "New per-row scalar realizer in a substrate function. Classification is an "
            + "indexed read on the id, and names resolve through realize.batch() over an "
            + "aggregated id array — one pass, not one render per row. Instead of here:\n  "
            + string.Join("\n  ", newcomers));
    }

    /// <summary>
    /// A migrated file must delete its allowlist entry, or the list fills with dead names
    /// and the ceiling stops meaning anything.
    /// </summary>
    [Fact]
    public void RenderBeforeSelect_Allowlist_HasNoStaleEntries()
    {
        var current = Violators();
        var stale = ScalarRealizerAllowlist.Where(a => !current.ContainsKey(a)).ToList();
        Assert.True(stale.Count == 0,
            $"These files no longer render per row — delete them from "
            + $"{nameof(ScalarRealizerAllowlist)} and lower {nameof(ScalarRealizerFileCeiling)} "
            + $"to {ScalarRealizerAllowlist.Count - stale.Count}:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void RenderBeforeSelect_AllowlistOnlyShrinks()
    {
        Assert.True(ScalarRealizerAllowlist.Count <= ScalarRealizerFileCeiling,
            $"{nameof(ScalarRealizerAllowlist)} has {ScalarRealizerAllowlist.Count} entries; "
            + $"ceiling is {ScalarRealizerFileCeiling}. This list may only shrink.");

        var sites = Violators().Values.Sum();
        Assert.True(sites <= ScalarRealizerSiteCeiling,
            $"{sites} scalar-realizer call sites across {Violators().Count} files; ceiling is "
            + $"{ScalarRealizerSiteCeiling}. An allowlisted file may not GROW its per-row "
            + "renders — aggregate the ids and join realize.batch() instead.");
    }
}
