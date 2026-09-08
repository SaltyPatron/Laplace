namespace Laplace.Engine.Core;

public static unsafe class CodepointPerfcache
{
    // Immutable mmap after load — cache base/count so hot readers skip the native gate.
    private static volatile bool _ready;
    private static CodepointRecord* _recs;
    private static int _count;

    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (LaplaceCoreGate.Native)
        {
            int rc = NativeInterop.CodepointTableLoadPerfcache(path);
            if (rc != 0)
            {
                string why = rc switch
                {
                    -1 => "open/stat/mmap failure (missing or unreadable file)",
                    -2 => "bad magic / unsupported format version",
                    -3 => "record count / size mismatch",
                    -4 => "body CRC mismatch (corrupt blob)",
                    -5 => "UCD version mismatch (stale or wrong Unicode authority)",
                    _ => "unknown error",
                };
                throw new InvalidOperationException(
                    $"codepoint_table_load_perfcache(\"{path}\") failed (rc={rc}): {why}");
            }
            PublishRecordsUnlocked();
        }
    }

    public static void LoadDefault()
    {
        if (_ready) return;
        lock (LaplaceCoreGate.Native)
        {
            if (_ready) return;
            if (IsLoadedUnlocked()) { PublishRecordsUnlocked(); return; }
            Load(ResolveDefaultPath());
        }
    }

    public static string ResolveDefaultPath() => LaplaceInstall.ResolveT0Perfcache();

    public static void Unload()
    {
        lock (LaplaceCoreGate.Native)
        {
            NativeInterop.CodepointTableUnload();
            _recs = null;
            _count = 0;
            _ready = false;
        }
    }

    public static bool IsLoaded => _ready;

    private static bool IsLoadedUnlocked() => NativeInterop.CodepointTableIsLoaded() != 0;

    private static void PublishRecordsUnlocked()
    {
        CodepointRecord* recs;
        ulong count;
        int rc = NativeInterop.CodepointTableRecords(&recs, &count);
        if (rc != 0)
            throw new InvalidOperationException(
                "codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
        // Native reverse lookup builds an index lazily. Complete that one-time
        // initialization under the same gate before publishing lock-free readers;
        // otherwise concurrent first lookups can race the index/count publication.
        // Every supported blob contains ASCII, including this non-NUL codepoint.
        var probe = recs['A'].Hash;
        uint codepoint;
        if (NativeInterop.CodepointTableLookupId(&probe, &codepoint) != 0 || codepoint != 'A')
            throw new InvalidOperationException("codepoint perf-cache reverse index initialization failed");
        _recs = recs;
        _count = checked((int)count);
        _ready = true;
    }

    public static ReadOnlySpan<CodepointRecord> Records
    {
        get
        {
            if (!_ready) throw new InvalidOperationException(
                "codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
            return new ReadOnlySpan<CodepointRecord>(_recs, _count);
        }
    }

    public static int Count
    {
        get
        {
            if (!_ready) throw new InvalidOperationException(
                "codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
            return _count;
        }
    }

    public static bool TryLookupCodepoint(Hash128 id, out uint codepoint)
    {
        codepoint = 0;
        if (!_ready) return false;
        Hash128 h = id;
        fixed (uint* pCp = &codepoint)
            return NativeInterop.CodepointTableLookupId(&h, pCp) == 0;
    }

    public static bool IsKnownCodepointId(Hash128 id) => TryLookupCodepoint(id, out _);

    /// <summary>One native batch over the mapped floor; zero bits remain unresolved.</summary>
    public static byte[] KnownIdsBitmap(IReadOnlyList<Hash128> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var bitmap = new byte[ids.Count / 8 + (ids.Count % 8 == 0 ? 0 : 1)];
        if (!_ready || ids.Count == 0) return bitmap;
        Hash128[] packed = ids as Hash128[] ?? ids.ToArray();
        fixed (Hash128* input = packed)
        fixed (byte* output = bitmap)
            if (NativeInterop.CodepointTablePresenceBitmap(
                    input, (nuint)packed.Length, output, (nuint)bitmap.Length) != 0)
                throw new InvalidOperationException("codepoint floor membership failed");
        return bitmap;
    }
}
