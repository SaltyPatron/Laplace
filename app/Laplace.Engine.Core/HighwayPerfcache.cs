using System.Text;

namespace Laplace.Engine.Core;

public static unsafe class HighwayPerfcache
{
    /// <summary>Returns the content-addressed hash for a highway node (POS tag, relation type, entity
    /// type name, etc.). Equivalent to <c>blake3(utf8_bytes)</c> — the canonical identity for any
    /// entity whose identity IS its surface content. Must match <c>highway_table.c</c> and
    /// <c>EntityTypeRegistry.Id()</c>.</summary>
    public static Hash128 NodeHash(ReadOnlySpan<byte> utf8) => Hash128.Blake3(utf8);

    /// <inheritdoc cref="NodeHash(ReadOnlySpan{byte})"/>
    public static Hash128 NodeHash(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        int maxBytes = Encoding.UTF8.GetMaxByteCount(name.Length);
        if (maxBytes <= 256)
        {
            Span<byte> buf = stackalloc byte[maxBytes];
            int n = Encoding.UTF8.GetBytes(name, buf);
            return Hash128.Blake3(buf.Slice(0, n));
        }
        return Hash128.Blake3(Encoding.UTF8.GetBytes(name));
    }

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
                    -2 => "bad magic / unsupported format version",
                    -3 => "record count / size mismatch",
                    _  => "unknown error",
                };
                throw new InvalidOperationException(
                    $"highway_table_load(\"{path}\") failed (rc={rc}): {why}");
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
        var env = Environment.GetEnvironmentVariable("LAPLACE_HIGHWAY_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        // Fall back to same env var used for all perfcache files; search build* dirs
        var perfEnv = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(perfEnv))
        {
            var dir = Path.GetDirectoryName(perfEnv);
            if (!string.IsNullOrEmpty(dir))
            {
                var hit = Path.Combine(dir, "laplace_highway_perfcache.bin");
                if (File.Exists(hit)) return hit;
            }
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName,
                    "laplace_highway_perfcache.bin", SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        throw new InvalidOperationException(
            "highway perfcache not found; build the engine or set LAPLACE_HIGHWAY_BIN.");
    }

    public static void Unload()
    {
        lock (LaplaceCoreGate.Native)
            NativeInterop.HighwayTableUnload();
    }

    public static bool IsLoaded
    {
        get { lock (LaplaceCoreGate.Native) return IsLoadedUnlocked(); }
    }

    private static bool IsLoadedUnlocked() => NativeInterop.HighwayTableIsLoaded() != 0;

    /// <summary>Returns the 256-bit mask for a given band index (0-12).
    /// Returns <see cref="Mask256.Zero"/> when the perfcache is not loaded.</summary>
    public static Mask256 BandMask(byte band)
    {
        lock (LaplaceCoreGate.Native)
        {
            Mask256 mask;
            NativeInterop.HighwayTableBandMask(band, &mask);
            return mask;
        }
    }

    /// <summary>Returns a single-bit mask for the relation type identified by <paramref name="typeId"/>,
    /// or <see cref="Mask256.Zero"/> when not found or perfcache not loaded.</summary>
    public static Mask256 MaskForRelationType(Hash128 typeId)
    {
        lock (LaplaceCoreGate.Native)
        {
            if (!IsLoadedUnlocked()) return Mask256.Zero;
            byte bit;
            float rank;
            byte band;
            int rc = NativeInterop.HighwayTableRelationByHash(&typeId, &bit, &rank, &band);
            return rc == 1 ? Mask256.Zero.Set(bit) : Mask256.Zero;
        }
    }

    /// <summary>Looks up rank and band for a given type-id.
    /// Returns false when not found or perfcache not loaded.</summary>
    public static bool TryGetRelation(Hash128 typeId, out byte bitPos, out float rank, out byte band)
    {
        lock (LaplaceCoreGate.Native)
        {
            if (!IsLoadedUnlocked()) { bitPos = 0; rank = 0; band = 0; return false; }
            byte bp; float r; byte b;
            int rc = NativeInterop.HighwayTableRelationByHash(&typeId, &bp, &r, &b);
            bitPos = bp; rank = r; band = b;
            return rc == 1;
        }
    }
}
