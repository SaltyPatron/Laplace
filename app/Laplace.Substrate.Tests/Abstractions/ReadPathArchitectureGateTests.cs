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
/// forces deleting its entry. Prose in CLAUDE.md is advisory — a gate is not.
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
    private static readonly Regex HandWrittenSql = new(
        @"\bCommandText\b|\bCreateCommand\s*\(\s*""|""\s*SELECT\s",
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
        "Laplace.Chess/Service/ChessEngineService.cs",
        "Laplace.Chess/Service/ChessLabRunners.cs",
        "Laplace.Chess/Service/ChessLiveGameHost.cs",
        "Laplace.Chess/Service/ChessPgnIngestor.cs",
        "Laplace.Chess.Uci/UciEngine.cs",
        "Laplace.Cli/ChessCommands.cs",
        "Laplace.Cli/DecompositionCommands.cs",
        "Laplace.Cli/FoundryCommands.cs",
        "Laplace.Cli/IngestCommands.cs",
        "Laplace.Cli/QueryCommands.cs",
        "Laplace.Endpoints.Mcp/SubstrateTools.cs",
        "Laplace.Migrations/Program.cs",
    };

    /// <summary>
    /// Files hand-writing SQL, 2026-07-26. THIS LIST MAY ONLY SHRINK.
    ///
    /// Migration order, by duplication density: SubstrateClient*, QueryCommands and
    /// SubstrateTools share 9 SQL functions between them, so one shared read surface
    /// retires all three at once. Chess/Service/* is 9 more files reading consensus and
    /// trajectory for evaluation. BillingPostgres/* is a genuinely separate concern
    /// (Stripe ledger, not substrate reads) and may legitimately stay hand-rolled.
    /// </summary>
    private static readonly HashSet<string> HandWrittenSqlAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Laplace.Chess/Service/ChessEngineService.cs",
        "Laplace.Chess/Service/ChessLiveGameHost.cs",
        "Laplace.Chess/Service/ChessMoveCommentary.cs",
        "Laplace.Chess/Service/ChessPgnIngestor.cs",
        "Laplace.Chess/Service/ChessWitnessHydrator.cs",
        "Laplace.Chess/Service/LearnedPst.cs",
        "Laplace.Chess/Service/SubstrateRootBias.cs",
        "Laplace.Chess/Service/SubstrateStateValuer.cs",
        "Laplace.Chess/Service/SubstrateTurnHost.cs",
        "Laplace.Cli/ContentRoundtrip.cs",
        "Laplace.Cli/CpuTopologyCommands.cs",
        "Laplace.Cli/FoundryCommands.cs",
        "Laplace.Cli/FoundryExport.cs",
        "Laplace.Cli/IngestCommands.cs",
        "Laplace.Cli/Provenance/ProvenanceExtractor.cs",
        "Laplace.Cli/QueryCommands.cs",
        "Laplace.Endpoints.Mcp/SubstrateTools.cs",
        "Laplace.Endpoints.OpenAICompat/AppComposition.cs",
        "Laplace.Endpoints.OpenAICompat/Auth/ApiKeys.cs",
        "Laplace.Endpoints.OpenAICompat/BillingBootstrap.cs",
        "Laplace.Endpoints.OpenAICompat/BillingPostgres/PostgresBillingEntitlementStore.cs",
        "Laplace.Endpoints.OpenAICompat/BillingPostgres/PostgresBillingLedger.cs",
        "Laplace.Endpoints.OpenAICompat/BillingPostgres/PostgresBillingQuoteStore.cs",
        "Laplace.Endpoints.OpenAICompat/BillingPostgres/PostgresStripePriceMap.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.Chess.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.Explore.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.Matchup.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.Mesh.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.Pulse.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.Query.cs",
        "Laplace.Endpoints.OpenAICompat/TurnWitness.cs",
        "Laplace.Migrations/Program.cs",
        "Laplace.Substrate/Abstractions/FeedbackContent.cs",
    };

    /// <summary>Ratchet ceilings, measured 2026-07-26. Lower as files migrate; never raise.</summary>
    private const int OwnDataSourceCeiling = 12;

    /// <inheritdoc cref="OwnDataSourceCeiling"/>
    /// <remarks>
    /// 34, not the 28 a line-oriented grep reports: six consumers hold their SQL in C#
    /// raw string literals, where the opening delimiter and the SELECT sit on different
    /// lines. The gate reads whole files, so it sees them. Trust this number over a grep.
    /// </remarks>
    private const int HandWrittenSqlCeiling = 34;

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
}
