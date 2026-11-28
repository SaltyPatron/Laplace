using System.Text.Json;
using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessFideLinkStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("laplace-fide-links-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task UpsertAsync_WritesPinnedSchemaMergesProvidersAndIsIdempotent()
    {
        var first = await ChessFideLinkStore.UpsertAsync("1503014", "chesscom", "MagnusCarlsen", _dir);
        Assert.True(first.Changed);
        Assert.Equal(Path.Combine(_dir, "player-links.json"), first.FilePath);

        var second = await ChessFideLinkStore.UpsertAsync("1503014", "lichess", "DrNykterstein", _dir);
        Assert.True(second.Changed);

        var third = await ChessFideLinkStore.UpsertAsync("1503014", "fide", "Carlsen,M", _dir);
        Assert.True(third.Changed);

        // Repeat save must be idempotent and not duplicate entries.
        var repeat = await ChessFideLinkStore.UpsertAsync("1503014", "chesscom", "MagnusCarlsen", _dir);
        Assert.False(repeat.Changed);

        // A second distinct player is preserved alongside the first.
        var other = await ChessFideLinkStore.UpsertAsync("2016192", "chesscom", "Hikaru", _dir);
        Assert.True(other.Changed);

        // A secondary account for the same FIDE ID appends a new entry without clobbering the first.
        var alt = await ChessFideLinkStore.UpsertAsync("1503014", "lichess", "DrDrunkenstein", _dir);
        Assert.True(alt.Changed);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(first.FilePath));
        var links = doc.RootElement.GetProperty("Links");
        Assert.Equal(3, links.GetArrayLength());

        var carlsen = links[0];
        Assert.Equal("1503014", carlsen.GetProperty("FideId").GetString());
        Assert.Equal("MagnusCarlsen", carlsen.GetProperty("ChessCom").GetString());
        Assert.Equal("DrNykterstein", carlsen.GetProperty("Lichess").GetString());
        Assert.Equal("Carlsen,M", carlsen.GetProperty("Alias").GetString());

        var hikaru = links[1];
        Assert.Equal("2016192", hikaru.GetProperty("FideId").GetString());
        Assert.Equal("Hikaru", hikaru.GetProperty("ChessCom").GetString());
        Assert.False(hikaru.TryGetProperty("Lichess", out _));

        var carlsenAlt = links[2];
        Assert.Equal("1503014", carlsenAlt.GetProperty("FideId").GetString());
        Assert.Equal("DrDrunkenstein", carlsenAlt.GetProperty("Lichess").GetString());
    }

    [Theory]
    [InlineData("", "chesscom", "MagnusCarlsen")]
    [InlineData("123", "chesscom", "MagnusCarlsen")]
    [InlineData("1234567890123", "chesscom", "MagnusCarlsen")]
    [InlineData("150301a", "chesscom", "MagnusCarlsen")]
    [InlineData("1503014", "unknown", "MagnusCarlsen")]
    [InlineData("1503014", "chesscom", "")]
    [InlineData("1503014", "chesscom", "bad\nname")]
    [InlineData("1503014", "chesscom", "bad\0name")]
    public async Task UpsertAsync_RejectsInvalidInputs(string fideId, string provider, string username)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ChessFideLinkStore.UpsertAsync(fideId, provider, username, _dir));
    }

    [Fact]
    public async Task UpsertAsync_RejectsOverlongUsername()
    {
        string tooLong = new('a', 257);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ChessFideLinkStore.UpsertAsync("1503014", "chesscom", tooLong, _dir));
    }

    [Fact]
    public async Task UpsertAsync_HandlesConcurrentWritesSafely()
    {
        var tasks = Enumerable.Range(0, 12).Select(i =>
            ChessFideLinkStore.UpsertAsync((1500000 + i).ToString(), i % 2 == 0 ? "chesscom" : "lichess", $"Player{i}", _dir));
        await Task.WhenAll(tasks);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "player-links.json")));
        Assert.Equal(12, doc.RootElement.GetProperty("Links").GetArrayLength());
    }

    [Fact]
    public async Task ChessGameRecorder_IngestFileAsync_PassesExplicitTrustClass()
    {
        var captured = new List<ChessNativeWitnessRequest>();
        string sample = Path.Combine(_dir, "sample.pgn");
        await File.WriteAllTextAsync(sample, "[Event \"Test\"]\n\n1. e4 e5 *\n");

        var recorder = new ChessGameRecorder(
            "Host=localhost;Database=laplace-test-none",
            _dir,
            (req, _, _, _) =>
            {
                captured.Add(req);
                return Task.FromResult(0);
            });

        await recorder.IngestFileAsync(ChessGameRecorder.Witness, sample);
        await recorder.IngestFileAsync("Chess.com", sample, ChessGameRecorder.CuratedCorpusTrustClass);
        await recorder.IngestSourceFileAsync(ChessGameRecorder.CuratedFideLinksSource, sample);

        Assert.Equal(3, captured.Count);
        Assert.Equal(new ChessNativeWitnessRequest("Laplace", "AppDerived", sample), captured[0]);
        Assert.Equal(new ChessNativeWitnessRequest("Chess.com", "StructuredCorpus", sample), captured[1]);
        Assert.Equal(new ChessNativeWitnessRequest(null, "AppDerived", sample, SourceName: "fide-links"), captured[2]);
    }
}
