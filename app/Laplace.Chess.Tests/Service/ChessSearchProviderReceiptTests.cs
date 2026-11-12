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

    [Fact]
    public void ReceiptCountsProvidersOnlyWhenSearchConsumesThem()
    {
        var configured = new ChessSearchConfiguration(substrate: true, rootBias: new RootBias());
        var search = configured.BuildSearch(ttBits: 10);

        var result = search.Think(
            Board.FromFen(ChessModality.StartFen),
            new Search.Limits(MaxDepth: 1, MaxNodes: 100_000, MaxTimeMs: 5_000));
        var solved = search.Think(
            Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1"),
            new Search.Limits(MaxDepth: 1, MaxNodes: 100_000, MaxTimeMs: 5_000));
        var receipt = configured.Receipt();

        Assert.NotNull(result.BestMove);
        Assert.NotNull(solved.BestMove);
        Assert.True(receipt.RootSteerReads > 0);
        Assert.True(receipt.RootMovesInfluenced > 0);
        Assert.Contains("syzygy=", receipt.Summary);
        Assert.DoesNotContain("position=", receipt.Summary);
    }

    [Fact]
    public void Configuration_CarriesTheContinuationTableIntoSearch()
    {
        var start = Board.FromFen(ChessModality.StartFen);
        var limits = new Search.Limits(MaxDepth: 3);
        var configured = new ChessSearchConfiguration(substrate: false, rootBias: null);
        var search = configured.BuildSearch(ttBits: 12);
        var cold = search.Think(start, limits);

        var table = new SearchContinuationTable();
        var replyBoard = start.Clone();
        MoveApply.Make(replyBoard, cold.BestMove!.Value);
        table.SeedActiveGame(replyBoard, [MoveGen.Legal(replyBoard)[0]]);
        configured.Continuations = table;
        configured.ApplyTo(search);
        var warm = search.Think(start, limits);

        Assert.Equal(cold.BestMove, warm.BestMove);
        Assert.Equal(cold.Score, warm.Score);
        Assert.True(warm.Nodes < cold.Nodes);
    }

    [Fact]
    public void ClassicalReceiptDoesNotClaimProviders()
    {
        var receipt = ChessSearchProviderReceipt.Classical;
        Assert.False(receipt.SubstrateSelected);
        Assert.Equal(0, receipt.RootSteerReads);
        Assert.Equal(0, receipt.SyzygyProbes);
        Assert.Equal("classical-only", receipt.Summary);
    }
    [Fact]
    public void SerialSearchConfigurationsReportColdHitAndExpiredProviderDeltas()
    {
        long clock = 0;
        int reads = 0;
        var bias = new SubstrateRootBias(_ =>
        {
            reads++;
            return Empty();
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
        var bias = new SubstrateRootBias(_ =>
        {
            if (++reads == 1) throw new InvalidOperationException("controlled backend failure");
            return Empty();
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
    public void ProfilingSummaryUsesInvariantMilliseconds()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var receipt = ChessSearchProviderReceipt.Classical with
            {
                SubstrateSelected = true, RootInclusiveMilliseconds = 1.25, RootEvidenceReadMilliseconds = 2.5,
            };
            Assert.Contains("root-ms-inclusive=1.250", receipt.Summary);
            Assert.Contains("evidence-read-ms=2.500", receipt.Summary);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static ChessSearchConfiguration Configuration(IRootBias? bias) => new(true, bias);

    private static IReadOnlyDictionary<Hash128, NpgsqlChessTransitionStandings.Row> Empty()
        => new Dictionary<Hash128, NpgsqlChessTransitionStandings.Row>();

}
