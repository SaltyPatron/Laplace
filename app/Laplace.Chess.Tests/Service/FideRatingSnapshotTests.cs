using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class FideRatingSnapshotTests
{
    [Fact]
    public async Task Snapshot_RoundTripsProjectedEstateAndRejectsTampering()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-fide-snapshot-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "rating-list.snapshot.json.gz");
        var fetchedAt = new DateTimeOffset(2026, 9, 4, 12, 34, 56, TimeSpan.Zero);
        const string archiveSha = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var players = new[]
        {
            new FideRatingList.Player(
                "2016192", "Nakamura, Hikaru", "USA", "M", "GM",
                2792, 2745, 2810, 1987, ""),
            new FideRatingList.Player(
                "1503014", "Carlsen, Magnus", "NOR", "M", "GM",
                2840, 2820, 2890, 1990, ""),
        };

        try
        {
            await FideRatingSnapshot.SaveAsync(path, players, fetchedAt, archiveSha, CancellationToken.None);

            var loaded = await FideRatingSnapshot.TryLoadAsync(path, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(fetchedAt, loaded!.FetchedAt);
            Assert.Equal(archiveSha, loaded.ArchiveSha256);
            Assert.Equal(new[] { "2016192", "1503014" }, loaded.Players.Select(p => p.FideId).ToArray());
            Assert.Equal("Nakamura, Hikaru", loaded.Players[0].Name);
            Assert.True(File.Exists(path + ".sha256"));

            await File.AppendAllTextAsync(path, "tamper");
            Assert.Null(await FideRatingSnapshot.TryLoadAsync(path, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Snapshot_InvalidChecksumSidecarFailsClosed()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-fide-snapshot-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "rating-list.snapshot.json.gz");
        const string archiveSha = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var players = new[]
        {
            new FideRatingList.Player(
                "2016192", "Nakamura, Hikaru", "USA", "M", "GM",
                2792, 2745, 2810, 1987, ""),
        };

        try
        {
            await FideRatingSnapshot.SaveAsync(
                path, players, DateTimeOffset.UtcNow, archiveSha, CancellationToken.None);
            await File.WriteAllTextAsync(path + ".sha256", "not-a-checksum\n");

            Assert.Null(await FideRatingSnapshot.TryLoadAsync(path, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
