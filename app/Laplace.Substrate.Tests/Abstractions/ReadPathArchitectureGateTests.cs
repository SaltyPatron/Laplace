using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Read/serve-path architecture gate — the counterpart to
/// <see cref="DecomposerArchitectureGateTests"/>, which made the WRITE path
/// uniform (Laplace.Decomposers: 99 files, zero Npgsql, zero inline SQL).
///
/// The read path never got the same treatment. 39 files across Cli, Chess,
/// Endpoints.* independently construct datasources and hand-write SQL against
/// the extension's 319 functions; 18 of those functions are called from two or
/// three consumers with separately hand-written binding and result mapping.
///
/// This gate does NOT demand that be fixed today. It RATCHETS: the current
/// violators are enumerated below, and the list may only ever shrink. New
/// hand-rolled substrate access fails the build; migrating a file requires
/// deleting its entry. That is what stops the sprawl from growing while the
/// shared read surface is built out underneath it.
///
/// The sanctioned home for Npgsql is app/Laplace.Substrate/Crud/Npgsql.
/// </summary>
public sealed class ReadPathArchitectureGateTests
{
    /// <summary>
    /// Direct datasource/connection/command construction, or raw SQL text.
    /// NpgsqlDataSourceBuilder is spelled out: a \b after "NpgsqlDataSource" does
    /// not match it, and it is the most common way the read path builds its own
    /// datasource (ChessLabRunners, DecompositionCommands).
    /// </summary>
    private static readonly Regex UnsanctionedDbAccess = new(
        @"\b(NpgsqlDataSourceBuilder|NpgsqlDataSource|new\s+NpgsqlConnection|new\s+NpgsqlCommand|CommandText)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Files that hand-roll substrate access as of 2026-07-26. THIS LIST MAY ONLY SHRINK.
    ///
    /// Migration order, by duplication density (shared SQL functions first):
    ///   1. SubstrateClient*.cs + QueryCommands.cs + SubstrateTools.cs — these three
    ///      independently call recall_session, walk_text, walk_branches, resolve_ref,
    ///      salient_facts, substrate_counts, entity_physicalities, consensus_out_readable,
    ///      word_id. Landing the shared read surface retires all three at once.
    ///   2. Chess/Service/* — 11 files, all reading consensus/trajectory for evaluation.
    ///   3. BillingPostgres/* — genuinely separate concern (Stripe ledger, not substrate
    ///      reads); may stay hand-rolled, but should move behind one billing store type.
    ///   4. Migrations/Program.cs — DbUp bootstrap, runs before the extension exists.
    ///      Likely a permanent, documented exception.
    /// </summary>
    private static readonly HashSet<string> HandRolledAccessAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Laplace.Chess/Service/ChessEngineService.cs",
        "Laplace.Chess/Service/ChessLabRunners.cs",
        "Laplace.Chess/Service/ChessLiveGameHost.cs",
        "Laplace.Chess/Service/ChessMoveCommentary.cs",
        "Laplace.Chess/Service/ChessPgnIngestor.cs",
        "Laplace.Chess/Service/ChessWitnessHydrator.cs",
        "Laplace.Chess/Service/LearnedPst.cs",
        "Laplace.Chess/Service/SubstrateRootBias.cs",
        "Laplace.Chess/Service/SubstrateStateValuer.cs",
        "Laplace.Chess/Service/SubstrateTurnHost.cs",
        "Laplace.Chess/Service/SubstructureFoldBias.cs",
        "Laplace.Chess.Uci/UciEngine.cs",
        "Laplace.Cli/ChessCommands.cs",
        "Laplace.Cli/ContentRoundtrip.cs",
        "Laplace.Cli/DecompositionCommands.cs",
        "Laplace.Cli/DocumentCommands.cs",
        "Laplace.Cli/EvalCommands.cs",
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
        "Laplace.Endpoints.OpenAICompat/BillingPostgres/PostgresBillingWebhookEventStore.cs",
        "Laplace.Endpoints.OpenAICompat/BillingPostgres/PostgresStripePriceMap.cs",
        "Laplace.Endpoints.OpenAICompat/EndpointMappings.Inference.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.Explore.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.Pulse.cs",
        "Laplace.Endpoints.OpenAICompat/SubstrateClient.Query.cs",
        "Laplace.Endpoints.OpenAICompat/TurnWitness.cs",
        "Laplace.Migrations/Program.cs",
        "Laplace.Substrate/Abstractions/FeedbackContent.cs",
    };

    /// <summary>
    /// The ratchet ceiling. Lower this as files migrate; never raise it.
    /// 39 = measured baseline on 2026-07-26.
    /// </summary>
    private const int AllowlistCeiling = 39;

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
            // The sanctioned home for Npgsql — this is where it is SUPPOSED to live.
            if (file.Contains(sanctioned, StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    private static string Rel(string repoRoot, string file) =>
        Path.GetRelativePath(Path.Combine(repoRoot, "app"), file).Replace('\\', '/');

    private static List<string> CurrentViolators(string repoRoot)
    {
        var found = new List<string>();
        foreach (var file in ScannedFiles(repoRoot))
        {
            if (UnsanctionedDbAccess.IsMatch(File.ReadAllText(file)))
                found.Add(Rel(repoRoot, file));
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    [Fact]
    public void ReadPath_NoNewHandRolledSubstrateAccess()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var newcomers = CurrentViolators(repoRoot)
            .Where(v => !HandRolledAccessAllowlist.Contains(v))
            .ToList();

        Assert.True(newcomers.Count == 0,
            "New hand-rolled substrate access. Go through the shared read surface in "
            + "app/Laplace.Substrate/Crud/Npgsql instead of opening a datasource here:\n  "
            + string.Join("\n  ", newcomers));
    }

    /// <summary>
    /// A file that migrated must delete its allowlist entry. Without this the list
    /// silently fills with dead names and the ceiling stops meaning anything.
    /// </summary>
    [Fact]
    public void ReadPath_AllowlistHasNoStaleEntries()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var current = CurrentViolators(repoRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = HandRolledAccessAllowlist.Where(a => !current.Contains(a)).ToList();

        Assert.True(stale.Count == 0,
            "These files no longer hand-roll substrate access — delete them from "
            + $"{nameof(HandRolledAccessAllowlist)} and lower {nameof(AllowlistCeiling)} "
            + $"to {HandRolledAccessAllowlist.Count - stale.Count}:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void ReadPath_AllowlistOnlyShrinks()
    {
        Assert.True(HandRolledAccessAllowlist.Count <= AllowlistCeiling,
            $"{nameof(HandRolledAccessAllowlist)} has {HandRolledAccessAllowlist.Count} entries but the "
            + $"ratchet ceiling is {AllowlistCeiling}. This list may only shrink — the read path is "
            + "being consolidated behind a shared surface, not expanded.");
    }
}
