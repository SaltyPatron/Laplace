namespace Laplace.Engine.Core;

public static unsafe class HighwayPerfcache
{
    // Load/unload mutate native state and stay behind the global gate. Lookups do
    // not: the table is an immutable mmap after load, and the mask lookup is on the
    // per-attestation staging path of every compose worker. The volatile flag is
    // published after a successful load inside the gate.
    private static volatile bool _loaded;

    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (LaplaceCoreGate.Native)
        {
            int rc = NativeInterop.HighwayTableLoad(path);
            if (rc != 0)
            {
                string why = rc switch
                {
                    -1 => "open/stat/mmap failure (missing or unreadable file)",
                    -2 => "invalid Native LPHW cache (version, checksum, descriptors or identifiers)",
                    _ => "unknown error",
                };
                throw new InvalidOperationException(
                    $"highway_table_load(\"{path}\") failed (rc={rc}): {why}");
            }
            _loaded = true;
        }
    }

    public static void LoadDefault()
    {
        lock (LaplaceCoreGate.Native)
        {
            if (IsLoadedUnlocked()) { _loaded = true; return; }
        }
        Load(ResolveDefaultPath());
    }

    public static string ResolveDefaultPath() => LaplaceInstall.ResolveHighwayPerfcache();

    public static void Unload()
    {
        lock (LaplaceCoreGate.Native)
        {
            _loaded = false;
            NativeInterop.HighwayTableUnload();
        }
    }

    public static bool IsLoaded => _loaded;

    public static ReadOnlySpan<HighwayRecord> Records
    {
        get
        {
            if (NativeInterop.HighwayTableRecords(out nint records, out ulong count) != 0)
                throw new InvalidOperationException("Native highway cache is not loaded.");
            return new ReadOnlySpan<HighwayRecord>((void*)records, checked((int)count));
        }
    }

    public static bool TryFind(Hash128 id, out HighwayRecord record)
    {
        nint pointer = NativeInterop.HighwayTableFind(&id);
        record = pointer != 0 ? *(HighwayRecord*)pointer : default;
        return pointer != 0;
    }

    public static bool TryLookup(string list, string key, out HighwayRecord record)
    {
        long slot = NativeInterop.HighwayTableKey(list, key);
        nint pointer = slot is >= 0 and <= uint.MaxValue
            ? NativeInterop.HighwayTableAt(list, (uint)slot) : 0;
        record = pointer != 0 ? *(HighwayRecord*)pointer : default;
        return pointer != 0;
    }

    public static byte[] Fingerprint()
    {
        var result = new byte[32];
        fixed (byte* bytes = result)
            if (NativeInterop.HighwayTableFingerprint(bytes) != 0)
                throw new InvalidOperationException("Native highway cache is not loaded.");
        return result;
    }

    public static uint[] Crosswalk(string from, uint slot, string to)
    {
        if (NativeInterop.HighwayTableEdgeSpan(from, slot, to, out nint edges, out ulong count) != 0)
            throw new InvalidOperationException("Native highway cache is not loaded.");
        var result = new uint[checked((int)count)];
        for (int i = 0; i < result.Length; i++) result[i] = ((uint*)edges)[i * 2 + 1];
        return result;
    }

    private static bool IsLoadedUnlocked() => NativeInterop.HighwayTableIsLoaded() != 0;

    public static Mask256 BandMask(byte band)
    {
        if (!_loaded) return Mask256.Zero;
        Mask256 mask;
        NativeInterop.HighwayTableBandMask(band, &mask);
        return mask;
    }

    public static Mask256 MaskForRelationType(Hash128 typeId)
    {
        // Lock-free: pure read of the immutable mmap'd table (see _loaded).
        if (!_loaded) return Mask256.Zero;
        byte bit;
        float rank;
        byte band;
        // highway_table_relation_by_hash returns 0 on success, -1 on miss.
        int rc = NativeInterop.HighwayTableRelationByHash(&typeId, &bit, &rank, &band);
        return rc == 0 ? Mask256.Zero.Set(bit) : Mask256.Zero;
    }

    public static bool TryGetRelation(Hash128 typeId, out byte bitPos, out float rank, out byte band)
    {
        if (!_loaded) { bitPos = 0; rank = 0; band = 0; return false; }
        byte bp = 0; float r = 0; byte b = 0;
        int rc = NativeInterop.HighwayTableRelationByHash(&typeId, &bp, &r, &b);
        bitPos = bp; rank = r; band = b;
        return rc == 0;
    }
}
