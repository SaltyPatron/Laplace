using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Unicode;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Cli;

internal static class Program
{
    private static string ConnString =>
        Environment.GetEnvironmentVariable("LAPLACE_DB")
        ?? "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: laplace <seed-unicode | decompose <text> | stats>");
            return 2;
        }
        try
        {
            return args[0] switch
            {
                "seed-unicode" => await SeedUnicodeAsync(),
                "decompose"    => Decompose(string.Join(' ', args[1..])),
                "stats"        => await StatsAsync(),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Fail(string m) { Console.Error.WriteLine(m); return 2; }

    // === seed-unicode: stream the T0 codepoint seed into the substrate ===
    private static async Task<int> SeedUnicodeAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var writer = new NpgsqlSubstrateWriter(ds);
        var reader = new NpgsqlSubstrateReader(ds);
        var dec = new UnicodeDecomposer(ResolveBlob());
        var ctx = new CliContext(writer, reader);

        Console.WriteLine($"seeding T0 codepoints into {ConnString} ...");
        var sw = Stopwatch.StartNew();

        await dec.InitializeAsync(ctx);
        long entities = 0, inserted = 0;
        int batches = 0;
        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            var r = await writer.ApplyAsync(change);
            entities += change.Entities.Length;
            inserted += r.EntitiesInserted;
            if (++batches % 16 == 0)
                Console.WriteLine($"  {entities,9:N0} codepoints applied ({sw.Elapsed.TotalSeconds:F0}s)");
        }
        sw.Stop();
        Console.WriteLine($"done: {entities:N0} codepoints presented, {inserted:N0} novel entities inserted in {sw.Elapsed.TotalSeconds:F1}s");

        await PrintCountsAsync(ds);
        return 0;
    }

    // === stats: current substrate row counts ===
    private static async Task<int> StatsAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await PrintCountsAsync(ds);
        return 0;
    }

    private static async Task PrintCountsAsync(NpgsqlDataSource ds)
    {
        await using var conn = await ds.OpenConnectionAsync();
        async Task<long> Scalar(string sql, byte[]? p = null)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = sql;
            if (p is not null) c.Parameters.AddWithValue("p", p);
            return (long)(await c.ExecuteScalarAsync())!;
        }
        long entities = await Scalar("SELECT count(*) FROM laplace.entities");
        long codepoints = await Scalar("SELECT count(*) FROM laplace.entities WHERE type_id = @p",
                                       UnicodeDecomposer.CodepointType.ToBytes());
        long phys = await Scalar("SELECT count(*) FROM laplace.physicalities");
        long content = await Scalar("SELECT count(*) FROM laplace.physicalities WHERE source_id = @p AND kind = 1",
                                    UnicodeDecomposer.Source.ToBytes());
        Console.WriteLine("substrate counts:");
        Console.WriteLine($"  entities total        : {entities,9:N0}");
        Console.WriteLine($"  └ Codepoint (T0)      : {codepoints,9:N0}");
        Console.WriteLine($"  physicalities total   : {phys,9:N0}");
        Console.WriteLine($"  └ UnicodeDecomposer CONTENT : {content,9:N0}");

        // Show a concrete row: U+0041 'A'.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT encode(p.entity_id,'hex'), e.tier,
                                   ST_X(p.coord), ST_Y(p.coord), ST_Z(p.coord), ST_M(p.coord),
                                   encode(p.hilbert_index,'hex')
                            FROM laplace.physicalities p JOIN laplace.entities e ON e.id = p.entity_id
                            WHERE p.source_id = @s AND p.kind = 1 AND p.entity_id = @e";
        cmd.Parameters.AddWithValue("s", UnicodeDecomposer.Source.ToBytes());
        // entity id of 'A' = BLAKE3-128 of UTF-8 "A"
        cmd.Parameters.AddWithValue("e", Hash128.Blake3(new byte[] { 0x41 }).ToBytes());
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (await rdr.ReadAsync())
        {
            Console.WriteLine("  sample U+0041 'A':");
            Console.WriteLine($"    entity id : {rdr.GetString(0)}  tier={rdr.GetInt16(1)}");
            Console.WriteLine($"    coord     : ({rdr.GetDouble(2):F6}, {rdr.GetDouble(3):F6}, {rdr.GetDouble(4):F6}, {rdr.GetDouble(5):F6})");
            Console.WriteLine($"    hilbert   : {rdr.GetString(6)}");
        }
        else
        {
            Console.WriteLine("  (no CONTENT physicality for U+0041 yet — run seed-unicode)");
        }
    }

    // === decompose: run the engine text decomposer + hash composer live ===
    private static int Decompose(string text)
    {
        if (string.IsNullOrEmpty(text)) return Fail("usage: laplace decompose <text>");
        CodepointPerfcache.Load(ResolveBlob());

        using var tree = TextDecomposer.Run(text);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

        Console.WriteLine($"decompose \"{text}\"  ({tree.NodeCount} nodes)\n");
        uint root = (uint)tree.NodeCount - 1;
        PrintNode(tree, root, 0);
        return 0;
    }

    private static readonly string[] TierName = { "CP", "GRAPHEME", "WORD", "SENTENCE", "DOC" };

    private static void PrintNode(TierTree tree, uint idx, int depth)
    {
        var v = tree.GetNode(idx);
        string label = v.Tier < TierName.Length ? TierName[v.Tier] : $"T{v.Tier}";
        string idHex;
        unsafe { idHex = $"{v.Id.Hi:x16}".Substring(0, 8); }
        string text = RenderLeaves(tree, idx).Replace("\n", "\\n");
        Console.WriteLine($"{new string(' ', depth * 2)}{label,-9} [{idHex}] \"{text}\"");
        if (v.Tier == 0) return;
        for (uint i = 0; i < v.ChildCount; i++)
            PrintNode(tree, v.FirstChildIdx + i, depth + 1);
    }

    private static string RenderLeaves(TierTree tree, uint idx)
    {
        var v = tree.GetNode(idx);
        if (v.Tier == 0) return char.ConvertFromUtf32((int)v.Atom);
        var sb = new StringBuilder();
        for (uint i = 0; i < v.ChildCount; i++) sb.Append(RenderLeaves(tree, v.FirstChildIdx + i));
        return sb.ToString();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PerfcacheResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        var recs = CodepointPerfcache.Records;
        if (atom >= (uint)recs.Length) return -1;
        ref readonly var r = ref recs[(int)atom];
        *outId = r.Hash;
        outCoord[0] = r.CoordX; outCoord[1] = r.CoordY; outCoord[2] = r.CoordZ; outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }

    private static string ResolveBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        const string share = "/opt/laplace/share/laplace";
        if (Directory.Exists(share))
        {
            var hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin").FirstOrDefault();
            if (hit is not null) return hit;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                                                   SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        throw new InvalidOperationException("perf-cache blob not found; build the engine or set LAPLACE_PERFCACHE_BIN.");
    }

    private sealed class CliContext(ISubstrateWriter writer, ISubstrateReader reader) : IDecomposerContext
    {
        public string EcosystemPath => "/vault/Data/Unicode";
        public ISubstrateWriter Writer { get; } = writer;
        public ISubstrateReader Reader { get; } = reader;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public string SubstrateVersion => "v0.1";
    }
}
