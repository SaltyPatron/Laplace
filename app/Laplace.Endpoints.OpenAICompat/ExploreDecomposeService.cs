using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class ExploreDecomposeService
{
    private static int _perfcacheLoaded;

    public DecomposeResponse Decompose(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        EnsurePerfcache();

        using var tree = TextDecomposer.Run(text);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

        var utf8 = Encoding.UTF8.GetBytes(text);
        var root = tree.GetNode(tree.NaturalUnitIndex());
        var nodes = new List<DecomposeNodeRow>((int)tree.NodeCount);

        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var n = tree.GetNode(i);
            nodes.Add(new DecomposeNodeRow(
                Ordinal: i,
                IdHex: Convert.ToHexStringLower(n.Id.ToBytes()),
                Label: Encoding.UTF8.GetString(utf8, (int)n.TextRangeOff, (int)n.TextRangeLen),
                Tier: n.Tier,
                TextOffset: (int)n.TextRangeOff,
                TextLength: (int)n.TextRangeLen));
        }

        return new DecomposeResponse(
            Text: text,
            RootIdHex: Convert.ToHexStringLower(root.Id.ToBytes()),
            NaturalUnitOrdinal: tree.NaturalUnitIndex(),
            Nodes: nodes);
    }

    private static void EnsurePerfcache()
    {
        if (Interlocked.CompareExchange(ref _perfcacheLoaded, 1, 0) != 0)
            return;
        CodepointPerfcache.Load(ResolvePerfcacheBlob());
    }

    private static string ResolvePerfcacheBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        }

        throw new InvalidOperationException(
            "T0 perfcache not found; build the engine or set LAPLACE_PERFCACHE_BIN.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int PerfcacheResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        var recs = CodepointPerfcache.Records;
        if (atom >= (uint)recs.Length) return -1;
        ref readonly var r = ref recs[(int)atom];
        *outId = r.Hash;
        outCoord[0] = r.CoordX;
        outCoord[1] = r.CoordY;
        outCoord[2] = r.CoordZ;
        outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }
}
