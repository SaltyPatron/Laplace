namespace Laplace.Engine.Core;

public static unsafe class CodepointPerfcache
{
    // Immutable mmap after load — cache base/count so hot readers skip the native gate.
    private static volatile bool _ready;
    private static CodepointRecord* _recs;
    private static int _count;
    private static int _recipeStreams;

    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (LaplaceCoreGate.Native)
        {
            // Native retains one immutable mapping. Validate an explicitly selected
            // alias instead of silently accepting a wrong path after an earlier load.
            int rc = NativeInterop.CodepointTableLoadPerfcache(path);
            if (rc != 0)
            {
                string why = rc switch
                {
                    -1 => "open/stat/mmap failure (missing or unreadable file)",
                    -3 => "expected exactly 1,114,112 Native 64-byte codepoint records",
                    -4 => "selected file differs from the canonical Native codepoint floor",
                    -5 => "canonical Native LAPLACE_TIER0 mapping unavailable",
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
            if (_recipeStreams != 0)
                throw new InvalidOperationException(
                    $"Cannot unload the codepoint perf-cache while {_recipeStreams} native recipe stream(s) hold it.");
            NativeInterop.CodepointTableUnload();
            _recs = null;
            _count = 0;
            _ready = false;
        }
    }

    // Creation and final release are the only shared synchronization boundaries
    // for native recipe streams. Their parser/tuple state has one local owner.
    internal static void RetainRecipeStream()
    {
        lock (LaplaceCoreGate.Native)
        {
            LoadDefault();
            _recipeStreams = checked(_recipeStreams + 1);
        }
    }

    internal static void ReleaseRecipeStream()
    {
        lock (LaplaceCoreGate.Native)
        {
            System.Diagnostics.Debug.Assert(_recipeStreams > 0);
            --_recipeStreams;
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
        // The mapped records are sorted by ID, not by scalar ordinal. Native
        // already validated and indexed the entire floor before publication.
        var ascii = NativeInterop.CodepointTableLookup('A');
        if (ascii == null) throw new InvalidOperationException("Native scalar floor has no ASCII A.");
        var probe = ascii->Hash;
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

    public static Hash128 Receipt
    {
        get
        {
            if (!_ready) throw new InvalidOperationException(
                "codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
            Hash128 receipt = default;
            if (NativeInterop.CodepointTableCopyReceipt(&receipt) != 0)
                throw new InvalidOperationException("codepoint perf-cache receipt unavailable");
            return receipt;
        }
    }

    public static string ReceiptHex => Convert.ToHexStringLower(Receipt.ToBytes());

    public static bool TryLookupCodepoint(Hash128 id, out uint codepoint)
    {
        codepoint = 0;
        if (!_ready) return false;
        Hash128 h = id;
        fixed (uint* pCp = &codepoint)
            return NativeInterop.CodepointTableLookupId(&h, pCp) == 0;
    }

    public static bool IsKnownCodepointId(Hash128 id) => TryLookupCodepoint(id, out _);

    public static bool TryLookupCodepoint(uint codepoint, out CodepointRecord record)
    {
        record = default;
        if (!_ready) return false;
        var entry = NativeInterop.CodepointTableLookup(codepoint);
        if (entry == null) return false;
        record = *entry; return true;
    }

    public static ref readonly CodepointRecord LookupCodepoint(uint codepoint)
    {
        if (!_ready) throw new InvalidOperationException("Load the Native codepoint floor before lookup.");
        var entry = NativeInterop.CodepointTableLookup(codepoint);
        if (entry == null) throw new ArgumentOutOfRangeException(nameof(codepoint), "Outside the Unicode codespace.");
        return ref *entry;
    }

    public static bool TryLookupScalar(uint scalar, out CodepointRecord record)
    {
        record = default;
        return scalar < 0x110000 && (scalar < 0xd800 || scalar > 0xdfff)
            && TryLookupCodepoint(scalar, out record);
    }

    public static ref readonly CodepointRecord LookupScalar(uint scalar)
    {
        if (scalar >= 0x110000 || scalar is >= 0xd800 and <= 0xdfff)
            throw new ArgumentOutOfRangeException(nameof(scalar), "Not a Unicode scalar.");
        return ref LookupCodepoint(scalar);
    }

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
