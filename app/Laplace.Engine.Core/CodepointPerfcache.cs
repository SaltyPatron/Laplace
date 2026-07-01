namespace Laplace.Engine.Core;

public static unsafe class CodepointPerfcache
{
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
                    _ => "unknown error",
                };
                throw new InvalidOperationException(
                    $"codepoint_table_load_perfcache(\"{path}\") failed (rc={rc}): {why}");
            }
        }
    }

    public static void LoadDefault()
    {
        lock (LaplaceCoreGate.Native)
        {
            if (IsLoadedUnlocked()) return;
            Load(ResolveDefaultPath());
        }
    }

    public static string ResolveDefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                                                   SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        throw new InvalidOperationException(
            "perf-cache blob not found; build the engine or set LAPLACE_PERFCACHE_BIN.");
    }

    public static void Unload()
    {
        lock (LaplaceCoreGate.Native)
            NativeInterop.CodepointTableUnload();
    }

    public static bool IsLoaded
    {
        get
        {
            lock (LaplaceCoreGate.Native)
                return IsLoadedUnlocked();
        }
    }

    private static bool IsLoadedUnlocked() => NativeInterop.CodepointTableIsLoaded() != 0;

    public static ReadOnlySpan<CodepointRecord> Records
    {
        get
        {
            lock (LaplaceCoreGate.Native)
            {
                CodepointRecord* recs;
                ulong count;
                int rc = NativeInterop.CodepointTableRecords(&recs, &count);
                if (rc != 0)
                    throw new InvalidOperationException(
                        "codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
                return new ReadOnlySpan<CodepointRecord>(recs, checked((int)count));
            }
        }
    }

    public static int Count
    {
        get
        {
            lock (LaplaceCoreGate.Native)
            {
                CodepointRecord* recs;
                ulong count;
                int rc = NativeInterop.CodepointTableRecords(&recs, &count);
                if (rc != 0)
                    throw new InvalidOperationException(
                        "codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
                return checked((int)count);
            }
        }
    }

    public static bool TryLookupCodepoint(Hash128 id, out uint codepoint)
    {
        codepoint = 0;
        lock (LaplaceCoreGate.Native)
        {
            if (!IsLoadedUnlocked()) return false;
            unsafe
            {
                Hash128 h = id;
                fixed (uint* pCp = &codepoint)
                    return NativeInterop.CodepointTableLookupId(&h, pCp) == 0;
            }
        }
    }

    public static bool IsKnownCodepointId(Hash128 id) => TryLookupCodepoint(id, out _);
}
