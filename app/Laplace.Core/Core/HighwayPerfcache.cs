using System.Runtime.InteropServices;
using System.Text;
using Laplace.Native;

namespace Laplace.Engine.Core;

public static unsafe class HighwayPerfcache
{
    private static volatile lp_highway* _highway;

    internal static lp_highway* Handle
    {
        get
        {
            LoadDefault();
            return _highway;
        }
    }

    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (LaplaceCoreGate.Native)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(path + '\0');
            lp_highway* mapped;
            fixed (byte* p = utf8)
                mapped = LaplaceNative.lp_highway_map(p);
            if (mapped == null)
                throw new InvalidOperationException($"lp_highway_map(\"{path}\") failed: missing or invalid LPHW cache.");
            _highway = mapped;
        }
    }

    public static void LoadDefault()
    {
        if (_highway != null) return;
        lock (LaplaceCoreGate.Native)
        {
            if (_highway != null) return;
            Load(ResolveDefaultPath());
        }
    }

    public static string ResolveDefaultPath()
    {
        try
        {
            return LaplaceInstall.ResolveHighwayPerfcache();
        }
        catch
        {
            byte* p = LaplaceNative.lp_highway_path();
            string? fallback = Marshal.PtrToStringUTF8((nint)p);
            if (!string.IsNullOrWhiteSpace(fallback) && File.Exists(fallback))
                return fallback;
            throw;
        }
    }

    public static void Unload()
    {
        lock (LaplaceCoreGate.Native)
            _highway = null;
    }

    public static bool IsLoaded => _highway != null;

    public static ReadOnlySpan<HighwayRecord> Records
    {
        get
        {
            var hw = _highway;
            if (hw == null) throw new InvalidOperationException("Native highway cache is not loaded.");
            return new ReadOnlySpan<HighwayRecord>(hw->rec, checked((int)hw->nrec));
        }
    }

    public static bool TryFind(Hash128 id, out HighwayRecord record)
    {
        var hw = _highway;
        if (hw == null) { record = default; return false; }
        lp_id nid = id.ToNative();
        lp_tier0_record* rec = LaplaceNative.lp_highway_find(hw, &nid);
        if (rec == null) { record = default; return false; }
        record = *(HighwayRecord*)rec;
        return true;
    }

    public static bool TryLookup(string list, string key, out HighwayRecord record)
    {
        var hw = _highway;
        if (hw == null) { record = default; return false; }
        byte[] listBytes = Encoding.UTF8.GetBytes(list + '\0');
        byte[] keyBytes = Encoding.UTF8.GetBytes(key + '\0');
        fixed (byte* pl = listBytes)
        fixed (byte* pk = keyBytes)
        {
            lp_list* l = LaplaceNative.lp_highway_list(hw, pl);
            if (l == null) { record = default; return false; }
            long slot = LaplaceNative.lp_highway_key(hw, l, pk);
            if (slot < 0 || slot > uint.MaxValue) { record = default; return false; }
            lp_tier0_record* rec = LaplaceNative.lp_highway_at(hw, l, (uint)slot);
            if (rec == null) { record = default; return false; }
            record = *(HighwayRecord*)rec;
            return true;
        }
    }

    public static byte[] Fingerprint()
    {
        var hw = _highway;
        if (hw == null) throw new InvalidOperationException("Native highway cache is not loaded.");
        var result = new byte[32];
        fixed (byte* bytes = result)
            LaplaceNative.lp_highway_fingerprint(hw, bytes);
        return result;
    }

    public static uint[] Crosswalk(string from, uint slot, string to)
    {
        var hw = _highway;
        if (hw == null) throw new InvalidOperationException("Native highway cache is not loaded.");
        byte[] fromBytes = Encoding.UTF8.GetBytes(from + '\0');
        byte[] toBytes = Encoding.UTF8.GetBytes(to + '\0');
        fixed (byte* pf = fromBytes)
        fixed (byte* pt = toBytes)
        {
            lp_edge* edges = null;
            nuint count = LaplaceNative.lp_highway_edges(hw, pf, slot, pt, &edges);
            if (count == 0 || edges == null) return [];
            var result = new uint[checked((int)count)];
            for (int i = 0; i < result.Length; i++)
                result[i] = edges[i].to;
            return result;
        }
    }

    public static ulong BankFieldMask(string bankName)
    {
        var hw = _highway;
        if (hw == null || string.IsNullOrWhiteSpace(bankName)) return 0UL;
        byte[] bBytes = Encoding.UTF8.GetBytes(bankName + '\0');
        fixed (byte* pb = bBytes)
        {
            lp_bank* bank = LaplaceNative.lp_highway_bank(hw, pb);
            return bank != null ? LaplaceNative.lp_mask_field(bank) : 0UL;
        }
    }

    public static ulong MaskForId(Hash128 id)
    {
        var hw = _highway;
        if (hw == null) return 0UL;
        lp_id nid = id.ToNative();
        ulong mask = 0;
        return LaplaceNative.lp_mask_add_id(hw, &mask, &nid) ? mask : 0UL;
    }
}
