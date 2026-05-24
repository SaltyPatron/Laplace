using System.Buffers.Binary;
using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.Ingestion;

/// <summary>
/// Append-only crash-tolerant journal of applied intent IDs. On restart
/// of a multi-hour ingest run the runner reads the journal once + uses
/// the resulting set to skip intents already applied — restart-safe at
/// the per-intent boundary.
///
/// <para>
/// On-disk format (little-endian fixed-width records):
/// <list type="bullet">
///   <item>Magic (8 bytes): "LPCKPT01"</item>
///   <item>Then a stream of 24-byte records: 16 bytes IntentId + 8 bytes
///         applied-at unix-microseconds</item>
/// </list>
/// </para>
///
/// <para>
/// Concurrent-safe: an in-process lock serialises Append + Flush; reads
/// are snapshot-based. The file is fsync'd on Flush.
/// </para>
/// </summary>
public sealed class CheckpointJournal : IAsyncDisposable
{
    private const string MagicString = "LPCKPT01";
    private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes(MagicString);
    private const int RecordBytes = 24;

    private readonly string _path;
    private readonly FileStream _stream;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<Hash128, byte> _applied;
    private long _pendingSinceLastFlush;

    private CheckpointJournal(string path, FileStream stream, ConcurrentDictionary<Hash128, byte> applied)
    {
        _path = path;
        _stream = stream;
        _applied = applied;
    }

    /// <summary>Open the journal at <paramref name="path"/> (creating the
    /// file with the magic header if missing). Reads the existing applied
    /// set into memory on open.</summary>
    public static async Task<CheckpointJournal> OpenOrCreateAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        FileStream stream;
        var applied = new ConcurrentDictionary<Hash128, byte>();

        var exists = File.Exists(path) && new FileInfo(path).Length > 0;
        stream = new FileStream(
            path,
            mode: FileMode.OpenOrCreate,
            access: FileAccess.ReadWrite,
            share: FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.WriteThrough);

        if (!exists)
        {
            await stream.WriteAsync(Magic.AsMemory(), ct);
            await stream.FlushAsync(ct);
        }
        else
        {
            // Verify magic
            var hdr = new byte[Magic.Length];
            stream.Position = 0;
            int read = await stream.ReadAsync(hdr.AsMemory(), ct);
            if (read != Magic.Length || !hdr.AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException(
                    $"checkpoint journal at {path} has invalid magic (expected {MagicString})");

            // Read records until EOF; tolerate a trailing partial record
            // (writer was killed mid-write — recover the consistent prefix).
            var buf = new byte[RecordBytes];
            while (true)
            {
                int n = await stream.ReadAsync(buf.AsMemory(), ct);
                if (n == 0) break;
                if (n < RecordBytes) break;  // partial record at tail → ignore
                var hi = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0, 8));
                var lo = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8, 8));
                applied.TryAdd(new Hash128(hi, lo), 1);
            }
            // Position at EOF for subsequent appends
            stream.Position = stream.Length;
        }

        return new CheckpointJournal(path, stream, applied);
    }

    /// <summary>True iff an intent with this id has been recorded in the
    /// journal (either from a prior run or from this run's appends).
    /// </summary>
    public bool WasApplied(Hash128 intentId) => _applied.ContainsKey(intentId);

    /// <summary>Number of applied intents currently in the journal.</summary>
    public int AppliedCount => _applied.Count;

    /// <summary>Path the journal is backed by.</summary>
    public string Path => _path;

    /// <summary>Record an intent as applied. Buffered until
    /// <see cref="FlushAsync"/> (which fsyncs); however the in-memory
    /// applied set updates immediately so concurrent workers see the
    /// effect without waiting for fsync.</summary>
    public async Task AppendAsync(Hash128 intentId, long appliedAtUnixUs, CancellationToken ct = default)
    {
        if (!_applied.TryAdd(intentId, 1)) return;  // already applied — no-op

        var buf = new byte[RecordBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), intentId.Hi);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8, 8), intentId.Lo);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), appliedAtUnixUs);

        lock (_writeLock)
        {
            _stream.Write(buf, 0, buf.Length);
            _pendingSinceLastFlush++;
        }
        await Task.CompletedTask;
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
    }

    /// <summary>Fsync the journal to durable storage. Call per the
    /// IngestRunner's flush-interval policy + once at the end of a run.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // FileStream.Flush(true) is the .NET equivalent of fsync.
        await Task.Run(() =>
        {
            lock (_writeLock)
            {
                _stream.Flush(flushToDisk: true);
                _pendingSinceLastFlush = 0;
            }
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { await FlushAsync(); }
        catch { /* best-effort on dispose */ }
        await _stream.DisposeAsync();
    }
}
