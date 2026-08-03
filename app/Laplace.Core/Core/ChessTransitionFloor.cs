using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Chess move/transition compose floor — (from_position, move) → to_position.
/// Deterministic ROM for state→state dedupe (operator law / GH #822 companion).
/// Not testimony. Not a ConcurrentDictionary presented as the ROM; the mmap blob is.
/// Process-lifetime novel hits accumulate in a side map so a run saturates like O(tier).
/// </summary>
public static unsafe class ChessTransitionFloor
{
    public const uint Magic = 0x5448434Cu; // 'LCHT'
    public const uint Version = 1;
    public const int HeaderSize = 64;
    public const int RecordSize = 32; // key16 + to16
    public const int TrailerBytes = 16;

    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static byte* _base;
    private static long _len;
    private static long _count;
    private static readonly ConcurrentDictionary<Hash128, Hash128> Novel = new();

    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Unload();
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < HeaderSize + TrailerBytes)
            throw new InvalidOperationException($"chess transition floor missing/short: {path}");

        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _len = fi.Length;
        byte* ptr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _base = ptr;

        if (ReadU32(0) != Magic || ReadU32(4) != Version)
        {
            Unload();
            throw new InvalidOperationException("bad chess transition floor magic/version");
        }
        _count = (long)ReadU64(8);
        long body = HeaderSize + _count * RecordSize;
        if (body + TrailerBytes > _len)
        {
            Unload();
            throw new InvalidOperationException("chess transition floor record layout mismatch");
        }
        var crc = Hash128.Blake3(new ReadOnlySpan<byte>(_base, (int)body));
        var stored = *(Hash128*)(_base + body);
        if (crc != stored)
        {
            Unload();
            throw new InvalidOperationException("chess transition floor body CRC mismatch");
        }
    }

    public static void LoadDefault()
    {
        if (_base != null) return;
        string? path = Environment.GetEnvironmentVariable("LAPLACE_CHESS_TRANSITION_BIN");
        if (string.IsNullOrEmpty(path))
        {
            try
            {
                string t0 = CodepointPerfcache.ResolveDefaultPath();
                string? dir = Path.GetDirectoryName(t0);
                if (dir != null)
                {
                    string cand = Path.Combine(dir, "laplace_chess_transition_perfcache.bin");
                    if (File.Exists(cand)) path = cand;
                }
            }
            catch { /* optional */ }
        }
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { Load(path); }
        catch { /* optional until emit configured */ }
    }

    public static void Unload()
    {
        if (_view != null && _base != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _count = 0;
        _len = 0;
        Novel.Clear();
    }

    public static bool IsLoaded => _base != null;
    public static long RecordCount => _count;
    public static int NovelCount => Novel.Count;

    public static bool TryLookup(Hash128 key, out Hash128 toId)
    {
        if (Novel.TryGetValue(key, out toId)) return true;
        toId = default;
        if (_base == null || _count == 0) return false;
        long lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            var rec = (TransitionRec*)(_base + HeaderSize + mid * RecordSize);
            int cmp = Compare(rec->Key, key);
            if (cmp == 0) { toId = rec->To; return true; }
            if (cmp < 0) lo = mid + 1;
            else
            {
                if (mid == 0) break;
                hi = mid - 1;
            }
        }
        return false;
    }

    /// <summary>Remember a novel transition for the rest of this process (run saturation).</summary>
    public static void Remember(Hash128 key, Hash128 toId) => Novel[key] = toId;

    public static void WriteBlob(string path, IReadOnlyList<(Hash128 Key, Hash128 To)> sortedUnique)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        long count = sortedUnique.Count;
        long body = HeaderSize + count * RecordSize;
        long total = body + TrailerBytes;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        fs.SetLength(total);
        using var mmf = MemoryMappedFile.CreateFromFile(fs, null, total, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        using var view = mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            *(uint*)ptr = Magic;
            *(uint*)(ptr + 4) = Version;
            *(ulong*)(ptr + 8) = (ulong)count;
            for (int i = 0; i < sortedUnique.Count; i++)
            {
                var rec = (TransitionRec*)(ptr + HeaderSize + i * RecordSize);
                rec->Key = sortedUnique[i].Key;
                rec->To = sortedUnique[i].To;
            }
            var crc = Hash128.Blake3(new ReadOnlySpan<byte>(ptr, (int)body));
            *(Hash128*)(ptr + body) = crc;
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static uint ReadU32(long off) => *(uint*)(_base + off);
    private static ulong ReadU64(long off) => *(ulong*)(_base + off);

    private static int Compare(Hash128 a, Hash128 b) => a.CompareToBytewise(b);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TransitionRec
    {
        public Hash128 Key;
        public Hash128 To;
    }
}
