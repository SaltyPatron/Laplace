using System.Security.Cryptography;

namespace Laplace.Engine.Core;

/// <summary>
/// Bounded external sort for deterministic transition records. Runs contain only fixed
/// 32-byte key/result pairs in native bytewise key order, followed by a SHA256 trailer. Equal pairs collapse; one key
/// with different results fails the entire build. This is an offline artifact producer,
/// not the process-local transition cache, and it is not thread safe.
/// </summary>
public sealed class ChessTransitionFloorBuilder : IDisposable
{
    public sealed record Result(ulong Records, ulong InputOccurrences, long PeakSpillBytes);

    private const int RunTrailerBytes = 32;
    private readonly string _directory;
    private readonly (Hash128 Key, Hash128 To)[] _buffer;
    private readonly int _fanIn;
    private readonly long _maximumSpillBytes;
    private int _buffered;
    private long _initialRuns;
    private long _spillBytes;
    private long _peakSpillBytes;
    private ulong _inputOccurrences;
    private bool _failed;
    private bool _completed;
    private bool _disposed;

    private static readonly IComparer<(Hash128 Key, Hash128 To)> RecordOrder =
        Comparer<(Hash128 Key, Hash128 To)>.Create((a, b) => a.Key.CompareToBytewise(b.Key));
    private static readonly IComparer<Hash128> KeyOrder =
        Comparer<Hash128>.Create((a, b) => a.CompareToBytewise(b));

    /// <param name="newWorkDirectory">An owned, new directory; disposal removes it.</param>
    /// <param name="maximumBufferedRecords">Maximum in-memory record array length.</param>
    /// <param name="mergeFanIn">Between 2 and 32; at most this many run readers are open.</param>
    /// <param name="maximumSpillBytes">Maximum simultaneously retained run bytes plus
    /// the unpublished final blob. Existing inputs, published output, filesystem allocation
    /// overhead, and OS page cache are not included in this logical byte grant.</param>
    public ChessTransitionFloorBuilder(string newWorkDirectory, int maximumBufferedRecords,
        int mergeFanIn, long maximumSpillBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(newWorkDirectory);
        if (maximumBufferedRecords < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBufferedRecords));
        if (mergeFanIn is < 2 or > 32)
            throw new ArgumentOutOfRangeException(nameof(mergeFanIn));
        if (maximumSpillBytes < ChessTransitionFloor.HeaderSize + ChessTransitionFloor.TrailerBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumSpillBytes));
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newWorkDirectory));
        if (Directory.Exists(_directory) || File.Exists(_directory))
            throw new IOException("Transition work directory must be new.");
        _buffer = new (Hash128 Key, Hash128 To)[maximumBufferedRecords];
        _fanIn = mergeFanIn;
        _maximumSpillBytes = maximumSpillBytes;
        Directory.CreateDirectory(_directory);
    }

    public void Add(Hash128 key, Hash128 to, CancellationToken ct = default)
    {
        EnsureActive();
        try
        {
            ct.ThrowIfCancellationRequested();
            _inputOccurrences = checked(_inputOccurrences + 1);
            _buffer[_buffered++] = (key, to);
            if (_buffered == _buffer.Length) FlushRun(ct);
        }
        catch { _failed = true; throw; }
    }

    /// <summary>Validate the complete existing v1 layout, checksum, count and strict
    /// ordering before adding any record. This never changes the serving floor.</summary>
    public void AddBlob(string path, CancellationToken ct = default)
    {
        EnsureActive();
        try { ChessTransitionFloor.VisitEntries(path, (key, to) => Add(key, to, ct), ct); }
        catch { _failed = true; throw; }
    }

    /// <summary>Finish disk merges and publish exactly one complete v1 file. The output
    /// must be outside the owned work directory so disposing runs cannot remove it.
    /// On refusal or cancellation the caller must dispose this builder and start anew.</summary>
    public Result Complete(string path, CancellationToken ct = default)
    {
        EnsureActive();
        try
        {
            ct.ThrowIfCancellationRequested();
            string output = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(output, _directory, comparison) ||
                output.StartsWith(_directory + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("Transition output must be outside the owned work directory.", nameof(path));

            FlushRun(ct);
            int pass = 0;
            long runCount = _initialRuns;
            // Names and scalar counts identify all runs. The number of input games/runs
            // never creates a growing in-memory collection of file metadata.
            while (runCount > 1)
            {
                long consumed = 0, produced = 0;
                while (consumed < runCount)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(_fanIn, runCount - consumed);
                    MergeRuns(pass, consumed, count, pass + 1, produced, ct);
                    consumed += count;
                    produced++;
                }
                pass++;
                runCount = produced;
            }

            string? finalRun = runCount == 0 ? null : RunPath(pass, 0);
            ulong records = finalRun is null ? 0 : checked((ulong)((RunLength(finalRun) - RunTrailerBytes) / ChessTransitionFloor.RecordSize));
            long finalBytes = checked(ChessTransitionFloor.HeaderSize +
                checked((long)records * ChessTransitionFloor.RecordSize) + ChessTransitionFloor.TrailerBytes);
            Reserve(finalBytes);
            try
            {
                IEnumerable<(Hash128 Key, Hash128 To)> stream = finalRun is null
                    ? Array.Empty<(Hash128 Key, Hash128 To)>() : ReadRun(finalRun, ct);
                ChessTransitionFloor.WriteBlob(output, stream, records, ct);
            }
            finally { _spillBytes -= finalBytes; }
            _completed = true;
            return new Result(records, _inputOccurrences, _peakSpillBytes);
        }
        catch { _failed = true; throw; }
    }

    private string RunPath(int pass, long index) => Path.Combine(_directory, $"p{pass:D3}-r{index:D20}.run");

    private void FlushRun(CancellationToken ct)
    {
        if (_buffered == 0) return;
        ct.ThrowIfCancellationRequested();
        Array.Sort(_buffer, 0, _buffered, RecordOrder);
        ct.ThrowIfCancellationRequested();
        using (var output = NewRun(RunPath(0, _initialRuns)))
        {
            bool havePrevious = false;
            (Hash128 Key, Hash128 To) previous = default;
            for (int i = 0; i < _buffered; i++)
            {
                ct.ThrowIfCancellationRequested();
                var current = _buffer[i];
                if (havePrevious && current.Key == previous.Key)
                {
                    if (current.To != previous.To) throw Conflict();
                    continue;
                }
                WriteRecord(output, current);
                previous = current;
                havePrevious = true;
            }
            output.Complete();
        }
        _buffered = 0;
        _initialRuns = checked(_initialRuns + 1);
    }

    private void MergeRuns(int inputPass, long first, int count, int outputPass,
        long outputIndex, CancellationToken ct)
    {
        var readers = new RunReader?[count];
        try
        {
            var queue = new PriorityQueue<int, Hash128>(count, KeyOrder);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var reader = new RunReader(RunPath(inputPass, first + i), ct);
                readers[i] = reader;
                if (reader.MoveNext(ct)) queue.Enqueue(i, reader.Current.Key);
            }
            using var output = NewRun(RunPath(outputPass, outputIndex));
            bool havePrevious = false;
            (Hash128 Key, Hash128 To) previous = default;
            while (queue.TryDequeue(out int index, out _))
            {
                ct.ThrowIfCancellationRequested();
                var reader = readers[index]!;
                var current = reader.Current;
                if (havePrevious && current.Key == previous.Key)
                {
                    if (current.To != previous.To) throw Conflict();
                }
                else
                {
                    WriteRecord(output, current);
                    previous = current;
                    havePrevious = true;
                }
                if (reader.MoveNext(ct)) queue.Enqueue(index, reader.Current.Key);
            }
            output.Complete();
        }
        finally { foreach (var reader in readers) reader?.Dispose(); }
        // Keep all source runs until the complete merged run has closed successfully.
        // Releasing each group here bounds concurrent disk use across every pass.
        for (int i = 0; i < count; i++)
        {
            string input = RunPath(inputPass, first + i);
            long bytes = RunLength(input);
            File.Delete(input);
            _spillBytes -= bytes;
        }
    }

    private RunWriter NewRun(string path) => new(this, path);

    private sealed class RunWriter : IDisposable
    {
        private readonly FileStream _stream;
        private readonly IncrementalHash _hash;
        public RunWriter(ChessTransitionFloorBuilder owner, string path)
        {
            // The trailer is admitted before the first record, not after a run has
            // already consumed the entire spill grant.
            owner.Reserve(RunTrailerBytes);
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.SequentialScan);
            try { _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); }
            catch { _stream.Dispose(); throw; }
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            _hash.AppendData(bytes);
            _stream.Write(bytes);
        }

        public void Complete() => _stream.Write(_hash.GetHashAndReset());

        public void Dispose()
        {
            try { _stream.Dispose(); }
            finally { _hash.Dispose(); }
        }
    }

    private void WriteRecord(RunWriter output, (Hash128 Key, Hash128 To) record)
    {
        Reserve(ChessTransitionFloor.RecordSize);
        Span<byte> bytes = stackalloc byte[ChessTransitionFloor.RecordSize];
        record.Key.WriteBytes(bytes);
        record.To.WriteBytes(bytes[16..]);
        output.Write(bytes);
    }

    private void Reserve(long bytes)
    {
        if (bytes < 0 || bytes > _maximumSpillBytes - _spillBytes)
            throw new IOException("Transition spill byte grant exhausted.");
        _spillBytes += bytes;
        _peakSpillBytes = Math.Max(_peakSpillBytes, _spillBytes);
    }

    private static InvalidOperationException Conflict() =>
        new("One transition key has conflicting deterministic results.");

    private static long RunLength(string path)
    {
        long length = new FileInfo(path).Length;
        if (length < RunTrailerBytes || (length - RunTrailerBytes) % ChessTransitionFloor.RecordSize != 0)
            throw new InvalidDataException("Transition run is not a whole number of checksummed fixed records.");
        return length;
    }

    private static IEnumerable<(Hash128 Key, Hash128 To)> ReadRun(string path, CancellationToken ct)
    {
        using var reader = new RunReader(path, ct);
        while (reader.MoveNext(ct)) yield return reader.Current;
    }

    private sealed class RunReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly byte[] _record = new byte[ChessTransitionFloor.RecordSize];
        private readonly byte[] _expectedHash;
        private readonly IncrementalHash _readHash;
        private long _remaining;
        private bool _havePrevious;
        private bool _finished;
        public (Hash128 Key, Hash128 To) Current { get; private set; }

        public RunReader(string path, CancellationToken ct)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            try
            {
                long bodyBytes = _stream.Length - RunTrailerBytes;
                if (bodyBytes < 0 || bodyBytes % ChessTransitionFloor.RecordSize != 0)
                    throw new InvalidDataException("Transition run is not a whole number of checksummed fixed records.");
                _remaining = bodyBytes / ChessTransitionFloor.RecordSize;
                // Validate the entire private run before exposing any accepted records.
                // One stack buffer and digest suffice regardless of run size.
                using var verify = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Span<byte> block = stackalloc byte[4096];
                for (long left = bodyBytes; left > 0;)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(left, block.Length);
                    _stream.ReadExactly(block[..count]);
                    verify.AppendData(block[..count]);
                    left -= count;
                }
                Span<byte> trailer = stackalloc byte[RunTrailerBytes];
                _stream.ReadExactly(trailer);
                _expectedHash = verify.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(_expectedHash, trailer) || _stream.ReadByte() != -1)
                    throw new InvalidDataException("Transition run SHA256 mismatch.");
                ct.ThrowIfCancellationRequested();
                _stream.Position = 0;
                _readHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }
            catch { _stream.Dispose(); throw; }
        }

        public bool MoveNext(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_remaining == 0)
            {
                if (!_finished)
                {
                    Span<byte> trailer = stackalloc byte[RunTrailerBytes];
                    _stream.ReadExactly(trailer);
                    if (!CryptographicOperations.FixedTimeEquals(_expectedHash, trailer)
                        || !CryptographicOperations.FixedTimeEquals(_expectedHash, _readHash.GetHashAndReset())
                        || _stream.ReadByte() != -1)
                        throw new InvalidDataException("Transition run changed during its read.");
                    _finished = true;
                }
                return false;
            }
            _stream.ReadExactly(_record);
            _readHash.AppendData(_record);
            var next = (Key: Hash128.FromBytes(_record), To: Hash128.FromBytes(_record.AsSpan(16)));
            if (_havePrevious && Current.Key.CompareToBytewise(next.Key) >= 0)
                throw new InvalidDataException("Transition run keys must be sorted and unique.");
            Current = next;
            _havePrevious = true;
            _remaining--;
            return true;
        }

        public void Dispose()
        {
            try { _stream.Dispose(); }
            finally { _readHash.Dispose(); }
        }
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed || _completed)
            throw new InvalidOperationException("Transition builder has already completed or failed.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Directory.Delete(_directory, recursive: true);
    }
}
