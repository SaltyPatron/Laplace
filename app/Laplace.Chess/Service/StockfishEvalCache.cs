using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// Persistent form of the census eval memo — the spec-33 two-tier pattern applied to
/// engine time: Postgres holds the system-of-record testimony; this file is a DERIVED,
/// one-way, versioned cache of pure function values (position id → side-to-move cp at a
/// fixed budget). It lives outside the database on purpose: a db-reset destroys the
/// testimony, the reseed re-derives it, and this cache makes that re-derivation pay
/// zero engine time for every position already searched. Header pins census version and
/// exact search budget — different budget = different testimony = cold cache.
/// </summary>
public static class StockfishEvalCache
{
    private const uint Magic = 0x4C505346; // "LPSF"
    private const int FormatVersion = 1;

    public static string DefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_CHESS_EVAL_CACHE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(baseDir, "laplace", "chess-eval-cache.bin");
    }

    public static ConcurrentDictionary<Hash128, int?> Load(
        string path, int censusVersion, int depth, long nodes)
    {
        var memo = new ConcurrentDictionary<Hash128, int?>();
        try
        {
            if (!File.Exists(path)) return memo;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != Magic || r.ReadInt32() != FormatVersion) return memo;
            if (r.ReadInt32() != censusVersion || r.ReadInt32() != depth || r.ReadInt64() != nodes)
                return memo; // different budget/version — the cached values are different testimony
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var id = Hash128.FromBytes(r.ReadBytes(16));
                bool has = r.ReadBoolean();
                int cp = r.ReadInt32();
                memo[id] = has ? cp : null;
            }
        }
        catch (Exception)
        {
            memo.Clear(); // torn/corrupt cache is worthless, never fatal — re-derive
        }
        return memo;
    }

    public static void Save(
        string path, int censusVersion, int depth, long nodes,
        ConcurrentDictionary<Hash128, int?> memo)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(censusVersion);
                w.Write(depth);
                w.Write(nodes);
                var snapshot = memo.ToArray();
                w.Write(snapshot.Length);
                foreach (var (id, cp) in snapshot)
                {
                    w.Write(id.ToBytes());
                    w.Write(cp.HasValue);
                    w.Write(cp ?? 0);
                }
            }
            File.Move(tmp, path, overwrite: true); // atomic on the same volume
        }
        catch (Exception)
        {
            // A failed cache save costs future engine time, never correctness.
        }
    }
}
