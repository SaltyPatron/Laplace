using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// Persistent form of the census eval memo — the spec-33 two-tier pattern applied to
/// engine time: Postgres holds the system-of-record testimony; this file is a DERIVED,
/// one-way, versioned cache of pure function values (position id → side-to-move cp at a
/// fixed budget). It lives outside the database on purpose: a db-reset destroys the
/// testimony, the reseed re-derives it, and this cache makes that re-derivation pay
/// zero engine time for every position already searched. Header pins census version and
/// exact search budget — different budget = different testimony = cold cache.
///
/// The compact snapshot is accompanied by a fixed-record append journal. A completed
/// Stockfish search is appended as soon as its line finishes, so canceling a long census
/// cannot throw away tens of minutes of paid engine work merely because the next full
/// snapshot threshold was not reached. Normal completion compacts the journal into the
/// snapshot; a torn journal tail is ignored record-by-record.
/// </summary>
public static class StockfishEvalCache
{
    private const uint Magic = 0x4C505346;        // "LPSF"
    private const uint JournalMagic = 0x4C50534A; // "LPSJ"
    private const int FormatVersion = 1;
    private const int JournalHeaderBytes = sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(int) + sizeof(long);
    private const int JournalRecordBytes = 16 + sizeof(bool) + sizeof(int);

    private static readonly ConcurrentDictionary<string, object> PathGates =
        new(StringComparer.Ordinal);

    public static string DefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_CHESS_EVAL_CACHE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(baseDir, "laplace", "chess-eval-cache.bin");
    }

    public static ConcurrentDictionary<Hash128, int?> Load(
        string path, int censusVersion, int depth, long nodes)
    {
        var memo = new ConcurrentDictionary<Hash128, int?>();
        lock (Gate(path))
        {
            LoadSnapshotInto(path, censusVersion, depth, nodes, memo);
            LoadJournalInto(JournalPath(path), censusVersion, depth, nodes, memo);
        }
        return memo;
    }

    /// <summary>
    /// Append only evaluations newly admitted to the run memo. Each record has fixed width,
    /// so a process killed during the final write leaves every preceding record readable and
    /// the incomplete tail is ignored on restart.
    /// </summary>
    public static void Append(
        string path, int censusVersion, int depth, long nodes,
        IReadOnlyCollection<KeyValuePair<Hash128, int?>> entries)
    {
        if (entries.Count == 0) return;
        try
        {
            lock (Gate(path))
            {
                string journal = JournalPath(path);
                string? directory = Path.GetDirectoryName(Path.GetFullPath(journal));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using var stream = new FileStream(
                    journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                using var rw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

                if (!JournalHeaderMatches(stream, censusVersion, depth, nodes))
                {
                    stream.SetLength(0);
                    stream.Position = 0;
                    WriteHeader(rw, JournalMagic, censusVersion, depth, nodes);
                }

                stream.Position = stream.Length;
                foreach (var (id, cp) in entries)
                {
                    rw.Write(id.ToBytes());
                    rw.Write(cp.HasValue);
                    rw.Write(cp ?? 0);
                }
                rw.Flush();
                stream.Flush(); // process-cancel durability; full fsync waits for compaction
            }
        }
        catch (Exception)
        {
            // A failed derived-cache append costs future engine time, never testimony.
        }
    }

    public static void Save(
        string path, int censusVersion, int depth, long nodes,
        ConcurrentDictionary<Hash128, int?> memo)
    {
        string? tmp = null;
        try
        {
            lock (Gate(path))
            {
                string full = Path.GetFullPath(path);
                string? directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                tmp = full + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

                using (var stream = new FileStream(
                    tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 128 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
                using (var w = new BinaryWriter(stream))
                {
                    WriteHeader(w, Magic, censusVersion, depth, nodes);
                    var snapshot = memo.ToArray();
                    w.Write(snapshot.Length);
                    foreach (var (id, cp) in snapshot)
                    {
                        w.Write(id.ToBytes());
                        w.Write(cp.HasValue);
                        w.Write(cp ?? 0);
                    }
                    w.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tmp, full, overwrite: true); // atomic on the same volume
                tmp = null;
                TryDelete(JournalPath(full));
            }
        }
        catch (Exception)
        {
            // A failed cache save costs future engine time, never correctness.
        }
        finally
        {
            if (tmp is not null) TryDelete(tmp);
        }
    }

    private static void LoadSnapshotInto(
        string path, int censusVersion, int depth, long nodes,
        ConcurrentDictionary<Hash128, int?> memo)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            if (!HeaderMatches(r, Magic, censusVersion, depth, nodes)) return;
            if (stream.Length - stream.Position < sizeof(int)) return;
            int count = r.ReadInt32();
            if (count < 0) return;
            for (int i = 0; i < count; i++)
            {
                if (stream.Length - stream.Position < JournalRecordBytes) return;
                var id = Hash128.FromBytes(r.ReadBytes(16));
                bool has = r.ReadBoolean();
                int cp = r.ReadInt32();
                memo[id] = has ? cp : null;
            }
        }
        catch (Exception)
        {
            // Snapshot corruption is isolated from the append journal. The journal may still
            // contain completed searches worth retaining.
        }
    }

    private static void LoadJournalInto(
        string journal, int censusVersion, int depth, long nodes,
        ConcurrentDictionary<Hash128, int?> memo)
    {
        if (!File.Exists(journal)) return;
        try
        {
            using var stream = File.OpenRead(journal);
            using var r = new BinaryReader(stream);
            if (!HeaderMatches(r, JournalMagic, censusVersion, depth, nodes)) return;
            while (stream.Length - stream.Position >= JournalRecordBytes)
            {
                var id = Hash128.FromBytes(r.ReadBytes(16));
                bool has = r.ReadBoolean();
                int cp = r.ReadInt32();
                memo[id] = has ? cp : null;
            }
        }
        catch (Exception)
        {
            // Fixed-width records before a bad/torn tail remain usable; any exception here
            // leaves the values already inserted into memo intact.
        }
    }

    private static bool JournalHeaderMatches(
        FileStream stream, int censusVersion, int depth, long nodes)
    {
        if (stream.Length < JournalHeaderBytes) return false;
        stream.Position = 0;
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        return HeaderMatches(r, JournalMagic, censusVersion, depth, nodes);
    }

    private static bool HeaderMatches(
        BinaryReader r, uint magic, int censusVersion, int depth, long nodes)
    {
        try
        {
            return r.ReadUInt32() == magic
                && r.ReadInt32() == FormatVersion
                && r.ReadInt32() == censusVersion
                && r.ReadInt32() == depth
                && r.ReadInt64() == nodes;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private static void WriteHeader(
        BinaryWriter w, uint magic, int censusVersion, int depth, long nodes)
    {
        w.Write(magic);
        w.Write(FormatVersion);
        w.Write(censusVersion);
        w.Write(depth);
        w.Write(nodes);
    }

    private static string JournalPath(string path) => Path.GetFullPath(path) + ".journal";

    private static object Gate(string path) =>
        PathGates.GetOrAdd(Path.GetFullPath(path), static _ => new object());

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
