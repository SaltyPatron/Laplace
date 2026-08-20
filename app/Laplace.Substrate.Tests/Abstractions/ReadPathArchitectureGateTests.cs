using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Read/serve-path architecture gate — the counterpart to
/// <see cref="DecomposerArchitectureGateTests"/>, which made the WRITE path uniform
/// (Laplace.Decomposers: 99 files, zero Npgsql, zero inline SQL).
///
/// The read path never got the same treatment, and it shows. Two DISTINCT problems,
/// gated separately because they have different fixes:
///
///   (a) Consumers construct their own datasource, bypassing the access policy.
///       Before <see cref="LaplaceDataSource"/> there were four wrappers around
///       LaplaceInstall.PostgresConnectionString(), and only ONE of them applied the
///       serving policy — so Chess's live-game and UCI hosts silently inherited the
///       ingest CLI's `Command Timeout=0`.
///
///   (b) Consumers hand-write SQL against the extension's 319 functions. 18 of those
///       functions are called from two or three consumers with separately written
///       binding and result mapping (recall_session, walk_text, walk_branches,
///       resolve_ref, salient_facts, substrate_counts, entity_physicalities,
///       consensus_out_readable, word_id).
///
/// Neither list is refactored here. Both RATCHET: current violators are enumerated and
/// the lists may only shrink. A new offender fails the build by name; migrating a file
/// forces deleting its entry. The executable check, not a prose claim, owns the roster.
///
/// The sanctioned home for both is app/Laplace.Substrate/Crud/Npgsql.
/// </summary>
public sealed class ReadPathArchitectureGateTests
{
    /// <summary>
    /// (a) Building your own datasource/connection. NpgsqlDataSourceBuilder is spelled
    /// out because a \b after "NpgsqlDataSource" does not match it, and it is how the
    /// read path actually builds datasources. Merely HOLDING an NpgsqlDataSource (a
    /// field type, a constructor parameter) is legitimate and not matched.
    /// </summary>
    private static readonly Regex OwnDataSourceConstruction = new(
        @"new\s+Npgsql(?:DataSourceBuilder|Connection)\s*\(",
        RegexOptions.Compiled);

    /// <summary>(b) Raw SQL text in a consumer.</summary>
    /// <remarks>
    /// Covers: CommandText assignments; CreateCommand with inline or named-const SQL;
    /// ordinary <c>" SELECT …"</c> literals; C# verbatim <c>@"…SELECT…"</c> (EvalCommands);
    /// and raw string literals <c>"""…SELECT…"""</c>. Mid-string SELECT inside a verbatim
    /// CTE was previously invisible to the gate.
    /// </remarks>
    private static readonly Regex HandWrittenSql = new(
        @"\bCommandText\b|\bCreateCommand\s*\(\s*(?:""|[A-Za-z_])|""\s*SELECT\s|@""[\s\S]*?\bSELECT\s|""""""[\s\S]*?\bSELECT\s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Files building their own datasource, 2026-07-26. THIS LIST MAY ONLY SHRINK.
    /// Route them through <c>LaplaceDataSource.Create(SubstrateAccess.Serving|Ingest)</c>.
    ///
    /// Laplace.Migrations/Program.cs is a likely PERMANENT exception: DbUp bootstrap runs
    /// against the maintenance database before the extension exists, so it cannot use a
    /// policy that assumes an installed substrate.
    /// </summary>
    private static readonly HashSet<string> OwnDataSourceAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Empty 2026-08-01: Chess + MCP route through LaplaceDataSource.Create.
        // Migrations / billing builders are outside the substrate scan set.
    };

    /// <summary>
    /// Files hand-writing SQL, 2026-07-26 (last redrawn 2026-08-01, doc 41). THIS LIST
    /// MAY ONLY SHRINK.
    ///
    /// 2026-08-01: drained via <c>NpgsqlConsensusByIds</c> (the Chess
    /// consensus.by_ids($1,$2) block, hand-copied in LearnedPst/SubstrateRootBias/
    /// SubstrateStateValuer/SubstrateTurnHost) and <c>NpgsqlSubstrateReads</c> (mesh_
    /// position/taxonomy_tree/band_leaders/entity_record/salient_facts/contrast/
    /// relation_summary/source_roster/modality_counts/substrate_pulse, which retired
    /// SubstrateClient.Mesh/.Matchup/.Pulse outright). Gate regex widened to catch
    /// <c>CreateCommand(sql)</c> with a named const; <c>EvalCommands.cs</c> tripped it and
    /// was drained same-day onto <c>NpgsqlRead.ReadRowsAsync</c> rather than joining the
    /// allowlist. <c>CpuTopologyCommands.cs</c> stays (conf-file generator, not a query).
    ///
    /// Same day: SubstrateClient.cs and SubstrateClient.Explore.cs (walk_text/completions,
    /// evidence/attestation batching, explain-trace steps, embedding lookup, top-relations,
    /// readiness/perf-cache probe, and every Explore.* resolver/neighbor/member/peer/
    /// container/consensus-web/label/facet/sense/constituent query) drained onto new
    /// <c>NpgsqlSubstrateReads</c> helpers, each translating PostgresException/
    /// NpgsqlException/TimeoutException via NpgsqlRead's onError delegate exactly like
    /// SubstrateClient's prior inline mapping. Both files deleted from this list.
    /// See doc .scratchpad/41_SQL_Standardization_Inventory.md for what is left.
    ///
    /// Same day (cluster 4): FeedbackContent → <c>NpgsqlConsensusCell</c>;
    /// ProvenanceExtractor circuit ENCODES → <c>BestOutboundBySubjectsAsync</c>
    /// (<c>edges_raw</c>); BandFacts / ChessFindPlayer / FoundryExport attribute plane
    /// off raw <c>laplace.consensus</c> onto <c>edges_raw</c>. Query/Chess stay
    /// allowlisted for other installed-function SQL. BillingPostgres/* is a genuinely
    /// separate concern (Stripe ledger, not substrate reads) and may legitimately stay
    /// hand-rolled.
    /// </summary>
    private static readonly HashSet<string> HandWrittenSqlAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Intentional substrate SQL escape hatch (MCP tool "sql").
        "Laplace.Endpoints.Mcp/SubstrateTools.cs",
    };

    /// <summary>Ratchet ceilings, measured 2026-07-26. Lower as files migrate; never raise.</summary>
    private const int OwnDataSourceCeiling = 0;

    /// <inheritdoc cref="OwnDataSourceCeiling"/>
    /// <remarks>
    /// Not what a naive line-oriented grep reports: several consumers hold their SQL in
    /// C# raw string literals, where the opening delimiter and the SELECT sit on
    /// different lines. The gate reads whole files, so it sees them. Trust this number
    /// over a grep.
    /// </remarks>
    private const int HandWrittenSqlCeiling = 1;

    private static IEnumerable<string> ScannedFiles(string repoRoot)
    {
        var appRoot = Path.Combine(repoRoot, "app");
        if (!Directory.Exists(appRoot)) yield break;

        var sanctioned = Path.Combine("Laplace.Substrate", "Crud", "Npgsql");
        var sep = Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
            if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
            // The sanctioned home — Npgsql is SUPPOSED to live here.
            if (file.Contains(sanctioned, StringComparison.OrdinalIgnoreCase)) continue;
            // Not substrate catalog: Stripe/app ledger, API-key store, DbUp bootstrap.
            if (file.Contains($"{sep}BillingPostgres{sep}", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{sep}Auth{sep}", StringComparison.OrdinalIgnoreCase)
                && file.Contains("OpenAICompat", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{sep}Laplace.Migrations{sep}", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.EndsWith($"{sep}BillingBootstrap.cs", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    private static List<string> Violators(string repoRoot, Regex rule)
    {
        var found = new List<string>();
        foreach (var file in ScannedFiles(repoRoot))
        {
            if (rule.IsMatch(File.ReadAllText(file)))
                found.Add(Path.GetRelativePath(Path.Combine(repoRoot, "app"), file).Replace('\\', '/'));
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    private static void AssertNoNewcomers(Regex rule, IReadOnlySet<string> allowlist, string guidance)
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var newcomers = Violators(repoRoot, rule).Where(v => !allowlist.Contains(v)).ToList();
        Assert.True(newcomers.Count == 0, guidance + "\n  " + string.Join("\n  ", newcomers));
    }

    private static void AssertNoStaleEntries(Regex rule, IReadOnlySet<string> allowlist, string listName)
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var current = Violators(repoRoot, rule).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = allowlist.Where(a => !current.Contains(a)).ToList();
        Assert.True(stale.Count == 0,
            $"These files no longer violate — delete them from {listName} and lower its "
            + $"ceiling to {allowlist.Count - stale.Count}:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void ReadPath_NoNewOwnDataSourceConstruction()
        => AssertNoNewcomers(OwnDataSourceConstruction, OwnDataSourceAllowlist,
            "New datasource built outside the sanctioned home. Use "
            + "LaplaceDataSource.Create(SubstrateAccess.Serving) for request/response paths "
            + "or SubstrateAccess.Ingest for CLI/ingest paths, instead of here:");

    [Fact]
    public void ReadPath_NoNewHandWrittenSql()
        => AssertNoNewcomers(HandWrittenSql, HandWrittenSqlAllowlist,
            "New hand-written SQL in a consumer. Add it to the shared read surface in "
            + "app/Laplace.Substrate/Crud/Npgsql so every caller gets one implementation, "
            + "instead of here:");

    /// <summary>
    /// A migrated file must delete its allowlist entry. Without this the lists fill with
    /// dead names and the ceilings stop meaning anything.
    /// </summary>
    [Fact]
    public void ReadPath_OwnDataSourceAllowlist_HasNoStaleEntries()
        => AssertNoStaleEntries(OwnDataSourceConstruction, OwnDataSourceAllowlist,
            nameof(OwnDataSourceAllowlist));

    /// <inheritdoc cref="ReadPath_OwnDataSourceAllowlist_HasNoStaleEntries"/>
    [Fact]
    public void ReadPath_HandWrittenSqlAllowlist_HasNoStaleEntries()
        => AssertNoStaleEntries(HandWrittenSql, HandWrittenSqlAllowlist,
            nameof(HandWrittenSqlAllowlist));

    [Fact]
    public void ReadPath_AllowlistsOnlyShrink()
    {
        Assert.True(OwnDataSourceAllowlist.Count <= OwnDataSourceCeiling,
            $"{nameof(OwnDataSourceAllowlist)} has {OwnDataSourceAllowlist.Count} entries; ceiling is "
            + $"{OwnDataSourceCeiling}. This list may only shrink.");
        Assert.True(HandWrittenSqlAllowlist.Count <= HandWrittenSqlCeiling,
            $"{nameof(HandWrittenSqlAllowlist)} has {HandWrittenSqlAllowlist.Count} entries; ceiling is "
            + $"{HandWrittenSqlCeiling}. This list may only shrink.");
    }

    [Fact]
    public void ReadPath_HasNoLegacyFixedInventoryOrTrajectoryTruncation()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var reads = File.ReadAllText(Path.Combine(repoRoot, "app", "Laplace.Substrate",
            "Crud", "Npgsql", "NpgsqlSubstrateReads.cs"));
        Assert.DoesNotMatch(@"\bLIMIT\s+(32|64|512)\b", reads);
    }

    [Fact]
    public void ConsensusWeb_HasNoGuessedFanoutOrFixedNodeCeiling()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var native = File.ReadAllText(Path.Combine(repoRoot, "extension",
            "laplace_substrate", "src", "explore_web.c"));

        Assert.DoesNotMatch(@"fanout\s*\*\s*2", native);
        Assert.DoesNotMatch(@"\b(fanout|max_nodes|hops)\s*=\s*(?:Min|Max)\b", native);
        Assert.DoesNotContain("max_nodes = 400", native, StringComparison.Ordinal);
        Assert.DoesNotContain("cand_cap = 4096", native, StringComparison.Ordinal);
        Assert.Contains("MaxAllocSize", native, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerationWalk_DerivesCapacityFromTheRequestedFrontier()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var native = File.ReadAllText(Path.Combine(repoRoot, "extension",
            "laplace_substrate", "src", "generate_walk.c"));

        Assert.DoesNotContain("GENERATE_NODE_BUDGET", native, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"beam\s*\*\s*3", native);
        Assert.Contains("(int64) n_frontier * (int64) beam", native, StringComparison.Ordinal);
        Assert.Contains("MaxAllocSize / sizeof(WalkNode)", native, StringComparison.Ordinal);
    }
}
