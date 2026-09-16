using System.Security.Cryptography;
using System.Text.Json;
using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;
using Inventory = Laplace.Chess.Service.ChessStartingSideInventory;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessStartingSideInventoryTests
{
    [Fact]
    public void ParseRequiresExplicitNewOutputAndPositiveBoundedOptions()
    {
        var options = Inventory.Parse(["--output-dir", "/build/inventory-evidence", "--page-size", "2",
            "--maximum-materialized-mib", "3", "--maximum-retained-mib", "4", "--deadline-seconds", "5"]);
        Assert.Equal(2, options.PageSize);
        Assert.Equal(3L * 1024 * 1024, options.MaterializedBytes);
        Assert.Equal(4L * 1024 * 1024, options.RetainedBytes);
        Assert.Equal(5, options.DeadlineSeconds);
        foreach (string[] bad in new string[][]
        {
            [], ["--output-dir"], ["--output-dir", "relative"],
            ["--output-dir", "/build/x", "--page-size", "0"],
            ["--output-dir", "/build/x", "--page-size", "257"],
            ["--output-dir", "/build/x", "--page-size", "-1"],
            ["--output-dir", "/build/x", "--deadline-seconds", "2147483647"],
            ["--output-dir", "/build/x", "--unknown", "1"],
            ["--output-dir", "/build/x", "--page-size", "1", "--page-size", "2"]
        })
            Assert.Throws<ArgumentException>(() => Inventory.Parse(bad));
        Assert.Throws<OverflowException>(() => Inventory.Parse(
            ["--output-dir", "/build/x", "--maximum-materialized-mib", long.MaxValue.ToString()]));
    }

    [Fact]
    public void ReadOnlyStartupPreservesExistingResolutionAndWinsOverEarlierDefault()
    {
        var result = new NpgsqlConnectionStringBuilder(Inventory.ReadOnlyConnectionString(
            "Host=/var/run/postgresql;Database=selected;Username=reader;Command Timeout=7;"
            + "Options='-c statement_timeout=10000 -c default_transaction_read_only=off'"));
        Assert.Equal("/var/run/postgresql", result.Host);
        Assert.Equal("selected", result.Database);
        Assert.Equal("reader", result.Username);
        Assert.Equal(7, result.CommandTimeout);
        Assert.EndsWith("-c default_transaction_read_only=on", result.Options);
        Assert.Contains("-c statement_timeout=10000", result.Options);
    }

    [Fact]
    public void StartingSideUsesVerifiedFirstNativePositionRatherThanMoveParity()
    {
        var white = Game(1, white: true);
        var black = Game(2, white: false);
        Assert.Equal("white", Inventory.StartingSide(white));
        Assert.Equal("black", Inventory.StartingSide(black));
        Assert.Throws<InvalidDataException>(() => Inventory.StartingSide(black with { StartPositionId = white.StartPositionId }));
        Assert.Throws<InvalidDataException>(() => Inventory.StartingSide(black with { StartPositionId = null }));
        Assert.Throws<InvalidDataException>(() => Inventory.StartingSide(black with { StartFen = null }));
        Assert.Equal("white", Inventory.StartingSide(white with { StartFen = null }));
    }

    [Fact]
    public async Task CompletePagedObservationRetainsBothSidesAllOwnersAndAuthenticatedInputs()
    {
        using var directory = new EvidenceDirectory();
        var games = new[] { Game(1, true), Game(2, false), Game(3, true) };
        var source = new ReadFixture(games);
        var report = await Inventory.CollectAsync(directory.Options(), source, CancellationToken.None);
        Assert.Equal("completed", report.Status);
        Assert.False(report.Snapshot);
        Assert.False(report.ProvesHistoricalCoverage);
        Assert.Equal("observed-read-interval", report.Scope);
        Assert.Equal(3, report.Retained);
        Assert.Equal(2, report.White);
        Assert.Equal(1, report.Black);
        Assert.Equal(0, report.Unclassified);
        Assert.True(report.ReachedEnd);
        Assert.Equal(games[2].PlayingId.ToString(), report.LastCompletedPageCursor);
        Assert.Equal(new[] { "", games[1].PlayingId.ToString(), games[2].PlayingId.ToString() }, source.Cursors);
        Assert.All(source.MaterializationGrants, grant => Assert.Equal(directory.Options().MaterializedBytes, grant));
        string[] lines = File.ReadAllLines(directory.Inputs);
        Assert.Equal(3, lines.Length);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("black", second.RootElement.GetProperty("StartingSide").GetString());
        Assert.Equal(2, second.RootElement.GetProperty("Sources").GetArrayLength());
        Assert.Equal(games[1].StartPositionId!.Value.ToString(),
            second.RootElement.GetProperty("StartPositionId").GetString());
        byte[] bytes = File.ReadAllBytes(directory.Inputs);
        Assert.Equal(bytes.LongLength, report.RetainedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), report.InputsSha256);
        Assert.True(report.FinishedUtc >= report.StartedUtc);
        Assert.Equal("completed", ReadStatus(directory));
    }

    [Fact]
    public async Task StrictHydrationFailureRetainsSelectedUnclassifiedIdsWithoutDefaultWhiteOrBudgetClaim()
    {
        using var directory = new EvidenceDirectory();
        var source = new ReadFixture([Game(1, false), Game(2, true)])
        { HydrationFailure = new InvalidDataException("Incomplete replay in the existing hydration window.") };
        var report = await Inventory.CollectAsync(directory.Options(), source, CancellationToken.None);
        Assert.Equal("partial", report.Status);
        Assert.Equal(0, report.White);
        Assert.Equal(0, report.Black);
        Assert.Equal(2, report.Unclassified);
        Assert.Equal(source.Games.Select(game => game.PlayingId.ToString()), report.PendingPlayingIds);
        Assert.Null(report.LastCompletedPageCursor);
        Assert.False(report.ReachedEnd);
        Assert.Equal("source-ownership-and-strict-hydration", report.Stage);
        Assert.Equal(nameof(InvalidDataException), report.FailureType);
        Assert.Contains("Incomplete replay", report.FailureDetail);
        Assert.DoesNotContain("budget", report.FailureDetail!);
        Assert.Equal(1024, report.HydrationReplayMaximumPlies);
        Assert.Empty(File.ReadAllBytes(directory.Inputs));
        Assert.Equal("partial", ReadStatus(directory));
    }

    [Fact]
    public async Task RetentionBoundKeepsOnlyWholeVerifiedRecordsAndExplicitPageRemainder()
    {
        using var first = new EvidenceDirectory();
        var games = new[] { Game(1, true), Game(2, false) };
        var one = await Inventory.CollectAsync(first.Options(), new ReadFixture([games[0]]), CancellationToken.None);
        using var bounded = new EvidenceDirectory();
        var report = await Inventory.CollectAsync(bounded.Options() with { RetainedBytes = one.RetainedBytes },
            new ReadFixture(games), CancellationToken.None);
        Assert.Equal("partial", report.Status);
        Assert.Equal(1, report.Retained);
        Assert.Equal(1, report.White);
        Assert.Equal(0, report.Black);
        Assert.Equal([games[1].PlayingId.ToString()], report.PendingPlayingIds);
        Assert.Equal(games[0].PlayingId.ToString(), report.LastRetainedCursor);
        Assert.Null(report.LastCompletedPageCursor);
        Assert.Single(File.ReadAllLines(bounded.Inputs));
        Assert.Equal(one.RetainedBytes, new FileInfo(bounded.Inputs).Length);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("count")]
    public async Task ChangedIdentityOrCountIsPartialEvenAfterEndOfEnumeration(string change)
    {
        using var directory = new EvidenceDirectory();
        var source = new ReadFixture([Game(1, true)]) { ChangeAfter = change };
        var report = await Inventory.CollectAsync(directory.Options(), source, CancellationToken.None);
        Assert.True(report.ReachedEnd);
        Assert.Equal(1, report.Retained);
        Assert.Equal("partial", report.Status);
        Assert.Equal(nameof(InvalidDataException), report.FailureType);
    }

    [Fact]
    public async Task DuplicateCursorDoesNotLoopOrClaimCompleteCoverage()
    {
        using var directory = new EvidenceDirectory();
        var source = new ReadFixture([Game(1, true), Game(2, false)]) { RepeatPage = true };
        var report = await Inventory.CollectAsync(directory.Options(), source, CancellationToken.None);
        Assert.Equal("partial", report.Status);
        Assert.Equal(2, source.Cursors.Count);
        Assert.Equal(2, report.Retained);
        Assert.Equal("page", report.Stage);
        Assert.Contains("repeated or unordered", report.FailureDetail);
    }

    [Fact]
    public async Task CancellationRetainsPartialReceiptAndExistingEvidenceCannotBeOverwritten()
    {
        using var directory = new EvidenceDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var report = await Inventory.CollectAsync(directory.Options(),
            new ReadFixture([Game(1, true)]), cancellation.Token);
        Assert.Equal("partial", report.Status);
        Assert.Equal(nameof(OperationCanceledException), report.FailureType);
        string previous = File.ReadAllText(directory.Summary);
        await Assert.ThrowsAsync<IOException>(() => Inventory.CollectAsync(directory.Options(),
            new ReadFixture([]), CancellationToken.None));
        Assert.Equal(previous, File.ReadAllText(directory.Summary));
    }

    private static ChessWitnessedGame Game(byte id, bool white)
    {
        string fen = ChessModality.StartFen.Replace(" w ", white ? " w " : " b ");
        var start = ChessCompose.PositionId(Board.FromFen(fen));
        var bytes = new byte[16];
        bytes[0] = id; // Fixtures are explicitly ordered by the real bytewise cursor contract.
        return new ChessWitnessedGame(start, Hash128.FromBytes(bytes), [], new GameOutcome(null),
            null, null, fen, null, null, null) { StartPositionId = start, MoveIds = [] };
    }

    private static string? ReadStatus(EvidenceDirectory directory)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(directory.Summary));
        return json.RootElement.GetProperty("Status").GetString();
    }

    // These protocol controls are not evidence of a live database inventory. Starting-side
    // identity checks above still exercise the existing native canonical position owner.
    private sealed class ReadFixture(ChessWitnessedGame[] games) : Inventory.IReadSource
    {
        public ChessWitnessedGame[] Games { get; } = games;
        public List<string> Cursors { get; } = [];
        public List<long> MaterializationGrants { get; } = [];
        public Exception? HydrationFailure { get; init; }
        public string? ChangeAfter { get; init; }
        public bool RepeatPage { get; init; }
        private int identityCalls, countCalls;

        public Task<Inventory.DatabaseIdentity> IdentityAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new Inventory.DatabaseIdentity("fixture",
                ++identityCalls > 1 && ChangeAfter == "identity" ? "changed" : "123", "456"));
        }
        public Task<long?> CountAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<long?>(Games.Length + (++countCalls > 1 && ChangeAfter == "count" ? 1 : 0));
        }
        public Task<IReadOnlyList<Hash128>> PageAsync(byte[] afterId, int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Cursors.Add(afterId.Length == 0 ? "" : Hash128.FromBytes(afterId).ToString());
            return Task.FromResult<IReadOnlyList<Hash128>>(Games
                .Where(game => RepeatPage || afterId.Length == 0 || Hash128.FromBytes(afterId).CompareToBytewise(game.PlayingId) < 0)
                .Take(limit).Select(game => game.PlayingId).ToArray());
        }
        public Task<Inventory.PageInputs> HydrateAsync(IReadOnlyList<Hash128> ids, long maximumBytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MaterializationGrants.Add(maximumBytes);
            if (HydrationFailure is { } failure) throw failure;
            var selected = Games.Where(game => ids.Contains(game.PlayingId)).ToArray();
            var owners = selected.ToDictionary(game => game.PlayingId,
                game => (IReadOnlyList<Inventory.SourceBinding>)[
                    new("ChessPgn", "fixture-pgn-source", game.LineId.ToString()),
                    new("ChessBook", "fixture-book-source", game.LineId.ToString())]);
            return Task.FromResult(new Inventory.PageInputs(selected, owners));
        }
    }

    private sealed class EvidenceDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")
            ?? throw new InvalidOperationException("TMPDIR must identify the permanent test workspace"),
            "chess-starting-side-inventory-" + Guid.NewGuid().ToString("N"));
        public string Inputs => Path.Combine(root, "hydrated-playings.jsonl");
        public string Summary => Path.Combine(root, "summary.json");
        public Inventory.Options Options() => new(root, 2, 8 * 1024 * 1024, 8 * 1024 * 1024, 30);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
