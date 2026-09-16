using System.Globalization;
using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessSearchProviderReceiptTests
{
    private sealed class RootBias : IRootBias
    {
        public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
        {
            var result = new int[moves.Count];
            if (result.Length > 0) result[0] = 25;
            return result;
        }
    }

    private sealed class PositionPlanes : IChessSearchPositionPlanes
    {
        public long Version => 7;
        public int Evaluate(Board board) => EvaluatePlanes(board).TotalCp;
        public ChessPositionPlaneScore EvaluatePlanes(Board board)
            => new(TotalCp: 20, AtomOutcomeCp: 5, LearnedPstCp: 12, TacticOutcomeCp: 3);
    }

    [Fact]
    public void ReceiptCountsProvidersOnlyWhenSearchConsumesThem()
    {
        var configured = new ChessSearchConfiguration(
            substrate: true,
            rootBias: new RootBias(),
            positionEvaluator: new PositionPlanes(),
            learnedSelected: true,
            learnedContributes: true,
            learnedNonZeroCells: 42,
            positionAtomsLoaded: 12,
            tacticSelected: true,
            tacticContributes: true,
            tacticPatternsLoaded: 7);
        var search = configured.BuildSearch(ttBits: 10);

        var result = search.Think(
            Board.FromFen(ChessModality.StartFen),
            new Search.Limits(MaxDepth: 1, MaxNodes: 100_000, MaxTimeMs: 5_000));
        var receipt = configured.Receipt();

        Assert.NotNull(result.BestMove);
        Assert.True(receipt.RootSteerReads > 0);
        Assert.True(receipt.RootMovesInfluenced > 0);
        Assert.True(receipt.PositionEvidenceReads > 0);
        Assert.True(receipt.PositionEvidenceContributions > 0);
        Assert.Equal(12, receipt.PositionAtomsLoaded);
        Assert.True(receipt.LearnedPstReads > 0);
        Assert.True(receipt.LearnedPstContributions > 0);
        Assert.Equal(42, receipt.LearnedPstNonZeroCells);
        Assert.True(receipt.TacticReads > 0);
        Assert.True(receipt.TacticContributions > 0);
        Assert.Equal(7, receipt.TacticPatternsLoaded);
        Assert.True(receipt.SyzygyProbes > 0);
        Assert.Contains("learned-pst=", receipt.Summary);
        Assert.Contains("tactics=", receipt.Summary);
    }

    [Fact]
    public void ClassicalReceiptDoesNotClaimProviders()
    {
        var receipt = ChessSearchProviderReceipt.Classical;
        Assert.False(receipt.SubstrateSelected);
        Assert.False(receipt.LearnedPstSelected);
        Assert.False(receipt.TacticSelected);
        Assert.Equal(0, receipt.RootSteerReads);
        Assert.Equal(0, receipt.PositionEvidenceReads);
        Assert.Equal(0, receipt.SyzygyProbes);
        Assert.Equal("classical-only", receipt.Summary);
    }
    [Fact]
    public void SerialSearchConfigurationsReportColdHitAndExpiredProviderDeltas()
    {
        long clock = 0;
        int reads = 0;
        var bias = new SubstrateRootBias((_, _, _, _) =>
        {
            reads++;
            return (Empty(), Empty());
        }, clock: () => clock);
        var board = Board.FromFen(ChessModality.StartFen);
        var moves = MoveGen.Legal(board);
        var first = Configuration(bias);
        Assert.Equal(0, first.Receipt().RootBackendReads);
        _ = first.RootBias!.Bonus(board, moves);
        var cold = first.Receipt();
        Assert.Equal("provider-instance-deltas", cold.RootWorkScope);
        Assert.Equal(1, cold.RootBackendReads);
        Assert.Equal(0, cold.RootEvidenceCacheHits);
        Assert.Equal(1, cold.RootFrontierBuilds);
        Assert.Equal(moves.Count, cold.RootTransitionPerfcacheHits
            + cold.RootTransitionNovelHits + cold.RootTransitionCompositions);
        Assert.True(cold.RootFrontierMilliseconds > 0);
        Assert.True(cold.RootEvidenceReadMilliseconds > 0);
        Assert.True(cold.RootInclusiveMilliseconds >=
            cold.RootFrontierMilliseconds + cold.RootEvidenceReadMilliseconds);

        var second = Configuration(bias);
        Assert.Equal(0, second.Receipt().RootSteerReads);
        _ = second.RootBias!.Bonus(board, moves);
        var hit = second.Receipt();
        Assert.Equal(1, hit.RootSteerReads);
        Assert.Equal(0, hit.RootBackendReads);
        Assert.Equal(1, hit.RootEvidenceCacheHits);
        Assert.Equal(0, hit.RootFrontierBuilds);
        Assert.Equal(0, hit.RootTransitionPerfcacheHits
            + hit.RootTransitionNovelHits + hit.RootTransitionCompositions);
        Assert.Equal(0, hit.RootFrontierMilliseconds);
        Assert.Equal(0, hit.RootEvidenceReadMilliseconds);

        clock = 2_001;
        var third = Configuration(bias);
        _ = third.RootBias!.Bonus(board, moves);
        var expired = third.Receipt();
        Assert.Equal(1, expired.RootBackendReads);
        Assert.Equal(0, expired.RootEvidenceCacheHits);
        Assert.Equal(0, expired.RootFrontierBuilds);
        Assert.Equal(0, expired.RootFrontierMilliseconds);
        Assert.Equal(2, reads);
        Assert.Equal(cold, first.Receipt());
        Assert.Equal(hit, second.Receipt());
        Assert.Contains("root-backend=1", cold.Summary);
        Assert.Contains("root-work-scope=provider-instance-deltas", cold.Summary);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(cold));
        Assert.Equal(1, json.RootElement.GetProperty(nameof(cold.RootBackendReads)).GetInt64());
        Assert.Equal("provider-instance-deltas",
            json.RootElement.GetProperty(nameof(cold.RootWorkScope)).GetString());
    }

    [Fact]
    public void FailedEvidenceReadRemainsInItsConfigurationAndRetryStartsAtZero()
    {
        int reads = 0;
        var bias = new SubstrateRootBias((_, _, _, _) =>
        {
            if (++reads == 1) throw new InvalidOperationException("controlled backend failure");
            return (Empty(), Empty());
        });
        var board = Board.FromFen(ChessModality.StartFen);
        var moves = MoveGen.Legal(board);
        var first = Configuration(bias);
        Assert.Throws<InvalidOperationException>(() => first.RootBias!.Bonus(board, moves));
        var failed = first.Receipt();
        Assert.Equal(1, failed.RootBackendReads);
        Assert.Equal(1, failed.RootFrontierBuilds);
        Assert.True(failed.RootEvidenceReadMilliseconds > 0);
        Assert.True(failed.RootInclusiveMilliseconds >= failed.RootEvidenceReadMilliseconds);
        var retry = Configuration(bias);
        Assert.Equal(0, retry.Receipt().RootBackendReads);
        _ = retry.RootBias!.Bonus(board, moves);
        Assert.Equal(1, retry.Receipt().RootBackendReads);
        Assert.Equal(2, bias.BackendReads);
        Assert.Equal(failed, first.Receipt());
    }

    [Fact]
    public void SnapshotPreparationCountsActualRefreshOutsideLeafEvaluationAndResetsPerConfiguration()
    {
        long epoch = 0;
        int loads = 0;
        var evaluator = new SubstrateBoardEvaluator(() =>
        {
            loads++;
            return new Dictionary<Hash128, (double EffMu, double Rd, double Witnesses)>();
        }, () => epoch);
        Assert.Equal(1, loads);
        var first = Configuration(null, evaluator);
        Assert.Equal(0, first.Receipt().SnapshotPreparations);
        epoch++;
        _ = first.PositionEvaluator!.PrepareSearch();
        var refreshed = first.Receipt();
        Assert.Equal(2, loads);
        Assert.Equal(1, refreshed.SnapshotPreparations);
        Assert.True(refreshed.SnapshotPreparationMilliseconds > 0);
        Assert.Equal(0, refreshed.PositionEvidenceReads);
        var second = Configuration(null, evaluator);
        _ = second.PositionEvaluator!.PrepareSearch();
        Assert.Equal(2, loads);
        Assert.Equal(1, second.Receipt().SnapshotPreparations);
        Assert.Equal(refreshed, first.Receipt());
    }

    [Fact]
    public void ProfilingSummaryUsesInvariantMilliseconds()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var receipt = ChessSearchProviderReceipt.Classical with
            {
                SubstrateSelected = true, RootInclusiveMilliseconds = 1.25,
                SnapshotPreparations = 1, SnapshotPreparationMilliseconds = 2.5,
            };
            Assert.Contains("root-ms-inclusive=1.250", receipt.Summary);
            Assert.Contains("snapshot-prepare=1/2.500ms", receipt.Summary);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static ChessSearchConfiguration Configuration(
        IRootBias? bias, ISearchPositionEvaluator? evaluator = null)
        => new(true, bias, evaluator, false, false, 0, positionAtomsLoaded: 0);

    private static IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Empty()
        => new Dictionary<Hash128, NpgsqlConsensusByIds.Row>();

}
