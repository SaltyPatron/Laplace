using System.Diagnostics;
using System.Globalization;
using System.Text;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;

namespace Laplace.Ops;

/// <summary>
/// Bounded CSV compatibility journal. Writers hold a cooperative cross-process
/// file lock only while appending or replacing the active file. No persistent
/// append handle can strand another writer on a rotated inode. JSONL remains
/// the independent structured event sink.
/// </summary>
internal sealed class RetainedCsvFileSink : ILogEventSink
{
    private readonly string _path, _directory, _archivePrefix, _pendingPrefix;
    private readonly ITextFormatter _formatter;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly TimeSpan _lockTimeout;

    internal RetainedCsvFileSink(string path, ITextFormatter formatter, long maxBytes, int retainedFiles,
        TimeSpan? lockTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFiles);
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path)!;
        _archivePrefix = Path.GetFileNameWithoutExtension(_path) + ".archive-";
        _pendingPrefix = "." + Path.GetFileName(_path) + ".pending-";
        _formatter = formatter;
        _maxBytes = maxBytes;
        _retainedFiles = retainedFiles;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        using var gate = AcquireWriterLock();
        using (var active = File.Open(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            if (active.Length > 0)
            {
                active.Seek(-1, SeekOrigin.End);
                if (active.ReadByte() != '\n')
                    throw new InvalidDataException($"Application log '{_path}' ends in an incomplete record; it was preserved without appending.");
            }
        }
        foreach (string pending in Directory.EnumerateFiles(_directory, _pendingPrefix + "*"))
            if (Guid.TryParseExact(Path.GetFileName(pending).AsSpan(_pendingPrefix.Length), "N", out _))
                SelfLog.WriteLine("CSV_INCOMPLETE_ROLLOVER active={0} preserved_pending={1}; inspect the interrupted replacement before declaring logging healthy.", _path, pending);
        PruneArchives();
    }

    public void Emit(LogEvent logEvent)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        _formatter.Format(logEvent, text);
        byte[] record = Encoding.UTF8.GetBytes(text.ToString());
        using var gate = AcquireWriterLock();
        long length = new FileInfo(_path).Length;
        if (length == 0 || length <= _maxBytes - record.LongLength)
        {
            Append(record);
            return;
        }

        // Write the complete first record before replacement. A SQL reader sees
        // the old file or the new one, never a copy/truncate window or a missing
        // active path. A single oversized event is retained intact.
        string pending = Path.Combine(_directory, _pendingPrefix + Guid.NewGuid().ToString("N"));
        var archives = FindArchives();
        long sequence = DateTime.UtcNow.Ticks;
        if (archives.Length > 0)
            sequence = Math.Max(sequence, checked(long.Parse(Path.GetFileName(archives[^1]).AsSpan(_archivePrefix.Length, 19),
                CultureInfo.InvariantCulture) + 1));
        string archive = Path.Combine(_directory, _archivePrefix
            + sequence.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            using (var replacement = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                replacement.Write(record);
            try { File.Replace(pending, _path, archive); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Windows readers and older append-only writers may deny delete
                // sharing. Keep accepting events on the active path, but expose
                // the failed retention through the shared SelfLog/health owner.
                Append(record);
                SelfLog.WriteLine("CSV_RETENTION_FAILED active={0} archive={1}: {2}", _path, archive, failure);
                return;
            }
            PruneArchives(archives.Append(archive).ToArray());
        }
        finally
        {
            // Keep an append/replacement failure as the original exception even
            // if cleanup also fails. Both causes reach the shared health owner.
            try { File.Delete(pending); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                SelfLog.WriteLine("CSV_REPLACEMENT_CLEANUP_FAILED path={0}: {1}", pending, failure);
            }
        }
    }

    private void Append(byte[] record)
    {
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Write(record);
    }

    private FileStream AcquireWriterLock()
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                // The lock file persists: deleting it would allow separate
                // processes to lock different inodes at the same pathname.
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate,
                    FileAccess.Write, FileShare.None);
            }
            catch (IOException failure)
            {
                if (Stopwatch.GetElapsedTime(started) >= _lockTimeout)
                    throw new IOException($"Cannot acquire application log writer lock '{_path}.lock'.", failure);
                Thread.Sleep(5);
            }
        }
    }

    private void PruneArchives(string[]? archives = null)
    {
        archives ??= FindArchives();
        foreach (string archive in archives.Take(Math.Max(0, archives.Length - (_retainedFiles - 1))))
            File.Delete(archive);
    }

    private string[] FindArchives()
    {
        // Restrict deletion to this sink's exact archive names; another role,
        // JSONL output, operator files and incomplete replacements are preserved.
        return Directory.EnumerateFiles(_directory, _archivePrefix + "*.csv")
            .Where(path =>
            {
                var suffix = Path.GetFileName(path).AsSpan(_archivePrefix.Length);
                return suffix.Length == 56 && suffix[19] == '-'
                    && long.TryParse(suffix[..19], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    && Guid.TryParseExact(suffix.Slice(20, 32), "N", out _);
            })
            .Order(StringComparer.Ordinal).ToArray();
    }
}
