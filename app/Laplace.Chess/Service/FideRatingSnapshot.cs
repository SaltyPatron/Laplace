using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// Durable, derived projection of one successfully parsed FIDE rating publication.
///
/// The XML/ZIP remains the provider artifact and the native XML grammar remains the
/// field authority. This snapshot only prevents an interactive read from paying the
/// provider download + complete grammar projection again after every API restart.
/// It is never admitted as an independent witness.
/// </summary>
internal static class FideRatingSnapshot
{
    private const int SnapshotVersion = 1;
    private const string SnapshotFileName = "rating-list.snapshot.json.gz";

    private sealed record Envelope(
        int Version,
        string SourceUrl,
        DateTimeOffset FetchedAt,
        string ArchiveSha256,
        FideRatingList.Player[] Players);

    internal sealed record Loaded(
        FideRatingList.Player[] Players,
        DateTimeOffset FetchedAt,
        string ArchiveSha256);

    internal static string DefaultPath
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("LAPLACE_FIDE_SNAPSHOT");
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured.Trim());

            try
            {
                return Path.Combine(
                    LaplaceInstall.ResolveChessGamesDir(),
                    "fide",
                    SnapshotFileName);
            }
            catch (InvalidOperationException)
            {
                // Developer/test machines are not required to mount /vault/Data. Keep the
                // derived cache outside the checkout rather than turning a missing corpus
                // mount into an unrelated FIDE read failure.
                string state = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(state)) state = Path.GetTempPath();
                return Path.Combine(state, "laplace", "chess", "fide", SnapshotFileName);
            }
        }
    }

    internal static async Task<Loaded?> TryLoadAsync(
        string path,
        CancellationToken ct)
    {
        string checksumPath = ChecksumPath(path);
        if (!File.Exists(path) || !File.Exists(checksumPath))
            return null;

        string expected = (await File.ReadAllTextAsync(checksumPath, ct).ConfigureAwait(false)).Trim();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            return null;

        string actual;
        await using (var raw = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            actual = Convert.ToHexString(
                await SHA256.HashDataAsync(raw, ct).ConfigureAwait(false));
        }

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return null;

        Envelope? envelope;
        try
        {
            await using var raw = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var gzip = new GZipStream(raw, CompressionMode.Decompress, leaveOpen: false);
            envelope = await JsonSerializer.DeserializeAsync<Envelope>(gzip, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope is null
            || envelope.Version != SnapshotVersion
            || !envelope.SourceUrl.Equals(FideRatingList.XmlArchiveUrl, StringComparison.Ordinal)
            || envelope.ArchiveSha256.Length != 64
            || !envelope.ArchiveSha256.All(Uri.IsHexDigit)
            || envelope.Players.Length == 0
            || envelope.Players.Any(static p =>
                p.FideId.Length is < 4 or > 12
                || !p.FideId.All(char.IsDigit)
                || string.IsNullOrWhiteSpace(p.Name)))
            return null;

        return new Loaded(envelope.Players, envelope.FetchedAt, envelope.ArchiveSha256);
    }

    internal static async Task SaveAsync(
        string path,
        FideRatingList.Player[] players,
        DateTimeOffset fetchedAt,
        string archiveSha256,
        CancellationToken ct)
    {
        if (players.Length == 0)
            throw new ArgumentException("FIDE snapshot cannot contain zero players.", nameof(players));
        if (archiveSha256.Length != 64 || !archiveSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("FIDE archive SHA-256 must be 64 hex characters.", nameof(archiveSha256));

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("FIDE snapshot path has no parent directory.");
        Directory.CreateDirectory(directory);

        string suffix = $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        string tempPath = fullPath + suffix;
        string checksumPath = ChecksumPath(fullPath);
        string checksumTempPath = checksumPath + suffix;

        try
        {
            var envelope = new Envelope(
                SnapshotVersion,
                FideRatingList.XmlArchiveUrl,
                fetchedAt,
                archiveSha256.ToUpperInvariant(),
                players);

            await using (var file = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await using (var gzip = new GZipStream(
                    file, CompressionLevel.Fastest, leaveOpen: true))
                {
                    await JsonSerializer.SerializeAsync(gzip, envelope, cancellationToken: ct)
                        .ConfigureAwait(false);
                }

                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            string checksum;
            await using (var raw = new FileStream(
                tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                checksum = Convert.ToHexString(
                    await SHA256.HashDataAsync(raw, ct).ConfigureAwait(false));
            }

            await File.WriteAllTextAsync(checksumTempPath, checksum + Environment.NewLine, ct)
                .ConfigureAwait(false);

            // Both files are complete before either active name changes. If the process is
            // killed between the two renames, checksum mismatch fails closed and the next
            // refresh rebuilds; no partial gzip is ever activated.
            File.Move(tempPath, fullPath, overwrite: true);
            File.Move(checksumTempPath, checksumPath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
            TryDelete(checksumTempPath);
        }
    }

    private static string ChecksumPath(string path) => path + ".sha256";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort. Active snapshot validation never trusts temp files.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
