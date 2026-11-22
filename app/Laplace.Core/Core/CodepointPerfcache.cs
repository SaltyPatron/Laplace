using System.Runtime.InteropServices;
using System.Text;
using Laplace.Native;

namespace Laplace.Engine.Core;

public static unsafe class CodepointPerfcache
{
    private const int CanonicalCodepointCount = 1_114_112;

    private static volatile bool _ready;
    private static lp_tier0_record* _recs;
    private static int _count;
    private static string? _loadedPath;
    private static lp_layout* _flagsLayout;
    private static lp_field* _fGcb;
    private static lp_field* _fWb;
    private static lp_field* _fSb;
    private static lp_field* _fIncb;
    private static lp_field* _fCcc;
    private static int _recipeStreams;

    internal static lp_tier0_record* TablePointer
    {
        get
        {
            LoadDefault();
            return _recs;
        }
    }

    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (LaplaceCoreGate.Native)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(path + '\0');
            lp_tier0_record* mapped;
            fixed (byte* p = utf8)
                mapped = LaplaceNative.lp_tier0_map(p);
            if (mapped == null)
                throw new InvalidOperationException($"lp_tier0_map(\"{path}\") failed: missing, malformed, or incomplete tier0.bin.");

            nuint count = LaplaceNative.lp_tier0_record_count(mapped);
            if (count != CanonicalCodepointCount)
                throw new InvalidOperationException($"lp_tier0_map(\"{path}\") returned {count} records, expected {CanonicalCodepointCount}.");

            _recs = mapped;
            _count = checked((int)count);
            _loadedPath = Path.GetFullPath(path);
            LoadFlagsBesideUnlocked(path);
            _ready = true;
        }
    }

    public static void LoadDefault()
    {
        if (_ready) return;
        lock (LaplaceCoreGate.Native)
        {
            if (_ready) return;
            Load(ResolveDefaultPath());
        }
    }

    public static string ResolveDefaultPath()
    {
        try
        {
            return LaplaceInstall.ResolveT0Perfcache();
        }
        catch
        {
            byte* p = LaplaceNative.lp_tier0_path();
            string? fallback = Marshal.PtrToStringUTF8((nint)p);
            if (!string.IsNullOrWhiteSpace(fallback) && File.Exists(fallback))
                return fallback;
            throw;
        }
    }

    public static void Unload()
    {
        lock (LaplaceCoreGate.Native)
        {
            if (_recipeStreams != 0)
                throw new InvalidOperationException(
                    $"Cannot unload the codepoint perf-cache while {_recipeStreams} native recipe stream(s) hold it.");
            _recs = null;
            _count = 0;
            _loadedPath = null;
            _flagsLayout = null;
            _fGcb = null;
            _fWb = null;
            _fSb = null;
            _fIncb = null;
            _fCcc = null;
            _ready = false;
        }
    }

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
            if (_recipeStreams > 0) --_recipeStreams;
        }
    }

    public static bool IsLoaded => _ready;

    public static ReadOnlySpan<CodepointRecord> Records
    {
        get
        {
            if (!_ready || _recs == null)
                throw new InvalidOperationException("codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
            return new ReadOnlySpan<CodepointRecord>(_recs, _count);
        }
    }

    public static int Count
    {
        get
        {
            if (!_ready || _recs == null)
                throw new InvalidOperationException("codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
            return _count;
        }
    }

    public static Hash128 Receipt
    {
        get
        {
            if (!_ready || _recs == null)
                throw new InvalidOperationException("codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
            byte* digest = stackalloc byte[32];
            LaplaceNative.lp_tier0_fingerprint(_recs, digest);
            return Hash128.FromBytes(new ReadOnlySpan<byte>(digest, 16));
        }
    }

    public static string ReceiptHex => Convert.ToHexStringLower(Receipt.ToBytes());

    public static bool TryLookupCodepoint(Hash128 id, out uint codepoint)
    {
        codepoint = 0;
        if (!_ready || _recs == null) return false;
        lp_id nativeId = id.ToNative();
        long cp = LaplaceNative.lp_tier0_codepoint(_recs, &nativeId);
        if (cp is < 0 or >= CanonicalCodepointCount) return false;
        codepoint = (uint)cp;
        return true;
    }

    public static bool IsKnownCodepointId(Hash128 id) => TryLookupCodepoint(id, out _);

    public static bool TryLookupCodepoint(uint codepoint, out CodepointRecord record)
    {
        record = default;
        if (!_ready || _recs == null) return false;
        lp_tier0_record* entry = LaplaceNative.lp_tier0_at(_recs, codepoint);
        if (entry == null) return false;
        record = *(CodepointRecord*)entry;
        return true;
    }

    public static ref readonly CodepointRecord LookupCodepoint(uint codepoint)
    {
        if (!_ready || _recs == null)
            throw new InvalidOperationException("Load the Native codepoint floor before lookup.");
        lp_tier0_record* entry = LaplaceNative.lp_tier0_at(_recs, codepoint);
        if (entry == null)
            throw new ArgumentOutOfRangeException(nameof(codepoint), "Outside the Unicode codespace.");
        return ref *(CodepointRecord*)entry;
    }

    public static bool TryLookupScalar(uint scalar, out CodepointRecord record)
    {
        record = default;
        return scalar < CanonicalCodepointCount && (scalar < 0xd800 || scalar > 0xdfff)
            && TryLookupCodepoint(scalar, out record);
    }

    public static ref readonly CodepointRecord LookupScalar(uint scalar)
    {
        if (scalar >= CanonicalCodepointCount || scalar is >= 0xd800 and <= 0xdfff)
            throw new ArgumentOutOfRangeException(nameof(scalar), "Not a Unicode scalar.");
        return ref LookupCodepoint(scalar);
    }

    public static uint GetFlagsWord(uint codepoint)
    {
        if (!_ready || _flagsLayout == null) return 0u;
        uint gcb = _fGcb != null ? LaplaceNative.lp_flags_get(_flagsLayout, codepoint, _fGcb) : 0u;
        uint wb = _fWb != null ? LaplaceNative.lp_flags_get(_flagsLayout, codepoint, _fWb) : 0u;
        uint sb = _fSb != null ? LaplaceNative.lp_flags_get(_flagsLayout, codepoint, _fSb) : 0u;
        uint incb = _fIncb != null ? LaplaceNative.lp_flags_get(_flagsLayout, codepoint, _fIncb) : 0u;
        uint ccc = _fCcc != null ? LaplaceNative.lp_flags_get(_flagsLayout, codepoint, _fCcc) : 0u;
        return (gcb & 0x0Fu)
             | ((wb & 0x1Fu) << 4)
             | ((sb & 0x0Fu) << 9)
             | ((incb & 0x03u) << 13)
             | ((ccc & 0xFFu) << 15);
    }

    public static byte[] KnownIdsBitmap(IReadOnlyList<Hash128> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var bitmap = new byte[ids.Count / 8 + (ids.Count % 8 == 0 ? 0 : 1)];
        if (!_ready || _recs == null || ids.Count == 0) return bitmap;
        for (int i = 0; i < ids.Count; i++)
        {
            lp_id nid = ids[i].ToNative();
            if (LaplaceNative.lp_tier0_contains(_recs, &nid))
                bitmap[i >> 3] |= (byte)(1 << (i & 7));
        }
        return bitmap;
    }

    private static void LoadFlagsBesideUnlocked(string tier0Path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(tier0Path));
        string? flagsPath = null;
        if (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "tier0.flags");
            if (File.Exists(candidate) && File.Exists(candidate + ".layout"))
                flagsPath = candidate;
        }
        if (flagsPath is null)
        {
            byte* p = LaplaceNative.lp_flags_path();
            string? fallback = Marshal.PtrToStringUTF8((nint)p);
            if (!string.IsNullOrWhiteSpace(fallback) && File.Exists(fallback))
                flagsPath = fallback;
        }
        if (flagsPath is null)
        {
            _flagsLayout = null;
            return;
        }
        byte[] fUtf8 = Encoding.UTF8.GetBytes(flagsPath + '\0');
        fixed (byte* pf = fUtf8)
            _flagsLayout = LaplaceNative.lp_flags_map(pf);
        if (_flagsLayout == null) return;

        fixed (byte* pGcb = "GCB\0"u8) _fGcb = LaplaceNative.lp_flags_field(_flagsLayout, pGcb);
        fixed (byte* pWb = "WB\0"u8) _fWb = LaplaceNative.lp_flags_field(_flagsLayout, pWb);
        fixed (byte* pSb = "SB\0"u8) _fSb = LaplaceNative.lp_flags_field(_flagsLayout, pSb);
        fixed (byte* pIncb = "InCB\0"u8) _fIncb = LaplaceNative.lp_flags_field(_flagsLayout, pIncb);
        fixed (byte* pCcc = "ccc\0"u8) _fCcc = LaplaceNative.lp_flags_field(_flagsLayout, pCcc);
    }
}
