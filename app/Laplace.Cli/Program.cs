using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.OMW;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.UD;
using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.FrameNet;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.VerbNet;
using Laplace.Decomposers.PropBank;
using Laplace.Decomposers.SemLink;
using Laplace.Decomposers.Unicode;
using Laplace.Decomposers.WordNet;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DynamicsInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Cli;

internal static class Program
{
    private static string ConnString
    {
        get
        {
            var s = Environment.GetEnvironmentVariable("LAPLACE_DB")
                ?? "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace-dev";
            if (!s.Contains("Include Error Detail", StringComparison.OrdinalIgnoreCase))
                s += ";Include Error Detail=true";
            if (!s.Contains("Search Path", StringComparison.OrdinalIgnoreCase))
                s += ";Search Path=laplace,public";
            return s;
        }
    }

    private static int EnvInt(string name, int fallback, int min)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var v) && v >= min ? v : fallback;
    }

    private static double EnvDouble(string name, double fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return double.TryParse(raw, out var v) && v >= 0 ? v : fallback;
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: laplace <command> [args]\n"
                + "  ingest <source> [path]            (unicode | iso639 | wordnet | omw | ud | model)\n"
                + "  synthesize substrate <recipe.json> [output.gguf] [--source-scope <ids>] [--format <name>]\n"
                + "  decompose <text>\n"
                + "  inspect <text>\n"
                + "  converse [prompt]                 (no prompt: REPL — one connection, one session)\n"
                + "  nn <word>                         (plural NN: structural geodesic + shape Fréchet + semantic μ)\n"
                + "  generate [prompt]                 (forward pass: ranked-μ walk over witnessed sequence; no prompt: REPL)\n"
                + "  roundtrip <file> [out]\n"
                + "  db-roundtrip <file>\n"
                + "  svd-exact-bench [model-dir] [tensor]  (prove tensor_svd_truncate is fp-exact on a real tensor; no DB)\n"
                + "  stats");
            return 2;
        }
        try
        {
            return args[0] switch
            {
                "ingest"       => await IngestAsync(args[1..]),
                "synthesize"   => await SynthesizeAsync(args[1..]),
                "decompose"    => Decompose(string.Join(' ', args[1..])),
                "inspect"      => await InspectAsync(string.Join(' ', args[1..])),
                "converse"     => await ConverseAsync(string.Join(' ', args[1..])),
                "cognize"      => await CognizeAsync(string.Join(' ', args[1..])),
                "nn"           => await NearestNeighborsAsync(string.Join(' ', args[1..])),
                "generate"     => await GenerateAsync(args[1..]),
                "roundtrip"    => Roundtrip(args.Length > 1 ? args[1] : "", args.Length > 2 ? args[2] : null),
                "db-roundtrip" => await DbRoundtripAsync(args.Length > 1 ? args[1] : ""),
                "stats"        => await StatsAsync(),
                "svd-exact-bench" => SvdExactBenchCmd(args[1..]),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Console.Error.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int Fail(string m) { Console.Error.WriteLine(m); return 2; }

    private static int SvdExactBenchCmd(string[] rest)
    {
        string modelDir = rest.Length > 0 && !string.IsNullOrEmpty(rest[0])
            ? rest[0]
            : ResolveTinyLlamaDir();
        string? tensor = rest.Length > 1 ? rest[1] : null;

        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
            return Fail("usage: laplace svd-exact-bench [model-dir] [tensor]\n" +
                        "  set $LAPLACE_TINYLLAMA_DIR or pass a model dir; none resolved.");

        bool pass = SvdExactBench.Run(modelDir, tensor);
        return pass ? 0 : 1;
    }

    private static string ResolveTinyLlamaDir()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_TINYLLAMA_DIR");
        if (!string.IsNullOrEmpty(env)) return env;

        const string root = "/vault/models";
        if (!Directory.Exists(root)) return "";
        var families = Directory.GetDirectories(root, "models--TinyLlama--*");
        foreach (var fam in families.OrderBy(f => f, StringComparer.Ordinal))
        {
            var snapsDir = Path.Combine(fam, "snapshots");
            if (!Directory.Exists(snapsDir)) continue;
            foreach (var snap in Directory.GetDirectories(snapsDir)
                                          .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                if (Directory.GetFiles(snap, "*.safetensors").Length > 0) return snap;
            }
        }
        return "";
    }

    private static async Task<int> DbRoundtripAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Fail($"usage: laplace db-roundtrip <file>  (not found: {path})");
        CodepointPerfcache.Load(ResolveBlob());
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var loggerFactory = ConsoleLoggerProvider.Factory();
        var inner = new NpgsqlSubstrateWriter(ds);
        await using var accumulator = new ConsensusAccumulatingWriter(inner, ds,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        ISubstrateWriter writer = accumulator;

        byte[] original = File.ReadAllBytes(path);

        var swR = Stopwatch.StartNew();
        await ContentRoundtrip.BootstrapAsync(writer);
        Hash128 docId = await ContentRoundtrip.RecordAsync(writer, original);
        swR.Stop();
        Console.WriteLine($"recorded : {original.Length,10:N0} bytes → document {Hex(docId)}  in {swR.Elapsed.TotalSeconds:F1}s");

        var swC = Stopwatch.StartNew();
        long materialized = await accumulator.MaterializeConsensusAsync();
        swC.Stop();
        Console.WriteLine($"consensus: {materialized,10:N0} relations from {accumulator.ObservationsAccumulated:N0} bigram matches in {swC.Elapsed.TotalSeconds:F1}s");

        var swX = Stopwatch.StartNew();
        byte[] rebuilt = await ContentRoundtrip.ReconstructAsync(ds, docId);
        swX.Stop();

        string hIn = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        string hOut = Convert.ToHexString(SHA256.HashData(rebuilt)).ToLowerInvariant();
        bool match = hIn == hOut;
        Console.WriteLine($"rebuilt  : {rebuilt.Length,10:N0} bytes read back FROM the database in {swX.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"sha256 in  : {hIn}");
        Console.WriteLine($"sha256 out : {hOut}");
        Console.WriteLine(match
            ? "BIT-PERFECT FROM DATABASE — reconstruction equals the original."
            : "MISMATCH — reconstruction differs.");
        return match ? 0 : 1;
    }

    private static string Hex(Hash128 h) => Convert.ToHexString(h.ToBytes()).ToLowerInvariant();

    private static Hash128 ReadHash16(byte[] b) =>
        new Hash128(BitConverter.ToUInt64(b, 0), BitConverter.ToUInt64(b, 8));

    private static async Task<int> ConverseAsync(string prompt)
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();

        if (!string.IsNullOrWhiteSpace(prompt))
            return await ConverseTurnAsync(conn, prompt);

        Console.WriteLine("laplace converse — one turn per line, empty line or Ctrl+D to leave.");
        while (true)
        {
            Console.Write("you      : ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            await ConverseTurnAsync(conn, line, echoPrompt: false);
        }
        return 0;
    }

    private static async Task<int> ConverseTurnAsync(NpgsqlConnection conn, string prompt, bool echoPrompt = true)
    {
        var sw = Stopwatch.StartNew();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT reply, eff_mu, witnesses FROM laplace.converse(@p)";
        cmd.Parameters.AddWithValue("p", prompt);
        await using var r = await cmd.ExecuteReaderAsync();
        if (echoPrompt) Console.WriteLine($"you      : {prompt}");
        bool any = false;
        while (await r.ReadAsync())
        {
            any = true;
            string reply = r.IsDBNull(0) ? "" : r.GetString(0);
            string mu = r.IsDBNull(1) ? "" : $"  μ={r.GetDecimal(1):F1}";
            string w = r.IsDBNull(2) ? "" : $" witnesses={r.GetInt64(2)}";
            Console.WriteLine($"substrate: {reply}{mu}{w}");
        }
        sw.Stop();
        if (!any) Console.WriteLine("substrate: (no reply rows)");
        Console.WriteLine($"           [{sw.Elapsed.TotalMilliseconds:F1} ms, one round-trip]");
        return 0;
    }

    private static async Task<int> CognizeAsync(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            Console.Error.WriteLine("usage: laplace cognize \"<goal>\"  (e.g. \"what is a dog\", \"how are whale and dolphin related\")");
            return 2;
        }
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();
        var sw = Stopwatch.StartNew();

        Console.WriteLine($"goal     : {goal}");

        Console.WriteLine("── answer ─────────────────────────────────────────");
        bool any = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT reply, eff_mu, witnesses FROM laplace.respond(@p)";
            cmd.Parameters.AddWithValue("p", goal);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                any = true;
                string reply = r.IsDBNull(0) ? "" : r.GetString(0);
                string mu = r.IsDBNull(1) ? "" : $"  μ={r.GetDecimal(1):F1}";
                string w = r.IsDBNull(2) ? "" : $" witnesses={r.GetInt64(2)}";
                Console.WriteLine($"  {reply}{mu}{w}");
            }
        }
        if (!any) Console.WriteLine("  (the substrate holds no answer to this yet)");

        Console.WriteLine("── gaps (unwitnessed arenas — the research agenda) ──");
        var gaps = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT missing_arena FROM laplace.gaps(laplace.resolve_last_word(@p))";
            cmd.Parameters.AddWithValue("p", goal);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) if (!r.IsDBNull(0)) gaps.Add(r.GetString(0));
        }
        Console.WriteLine(gaps.Count > 0
            ? $"  {string.Join(", ", gaps)}"
            : "  (none — every conceptual arena is witnessed)");

        sw.Stop();
        Console.WriteLine($"           [{sw.Elapsed.TotalMilliseconds:F1} ms, cognition over consensus reads]");
        return 0;
    }

    private static async Task<int> NearestNeighborsAsync(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return Fail("usage: laplace nn <word>");
        int k = EnvInt("LAPLACE_NN_K", 10, 1);

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();
        var sw = Stopwatch.StartNew();

        byte[]? id = null; string? coordHex = null, trajHex = null;
        await using (var res = conn.CreateCommand())
        {
            res.CommandText = @"
                SELECT p.entity_id, encode(ST_AsEWKB(p.coord),'hex'),
                       CASE WHEN p.trajectory IS NOT NULL THEN encode(ST_AsEWKB(p.trajectory),'hex') END
                FROM laplace.physicalities p
                JOIN laplace.prompt_state(@w) s ON p.entity_id = s.id
                WHERE p.type = 1 AND p.coord IS NOT NULL
                LIMIT 1";
            res.Parameters.AddWithValue("w", word);
            await using var r = await res.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                id = (byte[])r[0];
                coordHex = r.GetString(1);
                trajHex = r.IsDBNull(2) ? null : r.GetString(2);
            }
        }
        if (id is null || coordHex is null)
        {
            Console.WriteLine($"  ('{word}' is not a placed content entity in this substrate)");
            return 1;
        }

        Console.WriteLine($"\n  '{word}' — STRUCTURAL (glome geodesic) + SHAPE (Fréchet)");
        Console.WriteLine($"  {"neighbor",-26} {"geodesic",10} {"frechet",10}");
        Console.WriteLine($"  {new string('-', 26)} {new string('-', 10)} {new string('-', 10)}");
        bool anyStructural = false;
        await using (var st = conn.CreateCommand())
        {
            st.CommandText = @"
                WITH knn AS (
                    SELECT entity_id, coord, trajectory FROM laplace.physicalities
                    WHERE type = 1 ORDER BY coord <<->> @coord::geometry LIMIT GREATEST(@k*20, 200)),
                nearest AS (
                    SELECT DISTINCT ON (entity_id) entity_id, trajectory,
                           public.laplace_angular_distance_4d(coord, @coord::geometry) AS geo
                    FROM knn ORDER BY entity_id, public.laplace_angular_distance_4d(coord, @coord::geometry)),
                topk AS (
                    SELECT entity_id, trajectory, geo FROM nearest
                    WHERE entity_id <> @id ORDER BY geo LIMIT @k)
                SELECT laplace.render_text(entity_id, 24), geo,
                       CASE WHEN trajectory IS NOT NULL AND @traj <> ''
                            THEN public.laplace_frechet_4d(trajectory, @traj::geometry) END
                FROM topk ORDER BY geo";
            st.Parameters.AddWithValue("coord", coordHex);
            st.Parameters.AddWithValue("traj", (object?)trajHex ?? "");
            st.Parameters.AddWithValue("id", id);
            st.Parameters.AddWithValue("k", k);
            await using var r = await st.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                anyStructural = true;
                string nb = r.IsDBNull(0) ? "" : r.GetString(0);
                if (nb.Length > 26) nb = nb[..25] + "…";
                string g = r.IsDBNull(1) ? "" : r.GetDouble(1).ToString("F6");
                string f = r.IsDBNull(2) ? "—" : r.GetDouble(2).ToString("F4");
                Console.WriteLine($"  {nb,-26} {g,10} {f,10}");
            }
        }
        if (!anyStructural)
            Console.WriteLine($"  (‘{word}’ is not a placed content entity in this substrate)");

        if (id is not null)
        {
            Console.WriteLine($"\n  '{word}' — SEMANTIC (consensus μ via describe)");
            Console.WriteLine($"  {"type",-22} {"fact",-28} {"eff_mu",10} {"wit",4}");
            Console.WriteLine($"  {new string('-', 22)} {new string('-', 28)} {new string('-', 10)} {new string('-', 4)}");
            await using var se = conn.CreateCommand();
            se.CommandText =
                "SELECT type, fact, round(eff_mu,0)::bigint, witnesses "
                + "FROM laplace.describe(@id) ORDER BY eff_mu DESC LIMIT @k";
            se.Parameters.AddWithValue("id", id);
            se.Parameters.AddWithValue("k", k);
            await using var r = await se.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string kind = r.IsDBNull(0) ? "" : r.GetString(0);
                string fact = r.IsDBNull(1) ? "" : r.GetString(1);
                if (fact.Length > 28) fact = fact[..27] + "…";
                string mu = r.IsDBNull(2) ? "" : r.GetInt64(2).ToString("N0");
                string wit = r.IsDBNull(3) ? "" : r.GetInt64(3).ToString();
                Console.WriteLine($"  {kind,-22} {fact,-28} {mu,10} {wit,4}");
            }
        }

        sw.Stop();
        Console.WriteLine($"\n  [{sw.Elapsed.TotalMilliseconds:F1} ms — two co-equal axes, read-only, no GPU]\n");
        return 0;
    }

    private static readonly string[] GenStop =
    {
        "the","a","an","and","or","but","of","to","in","on","at","by","for","with","as",
        "is","was","are","were","be","been","have","has","had","do","did","will","would",
        "can","could","may","might","must","i","you","he","she","it","we","they","not",
        "no","so","this","that","which","who","what","all","one","him","her","them","its","his",
    };

    private const string GenerateSql = @"
        WITH RECURSIVE
        stops AS (SELECT DISTINCT p.id FROM unnest(@stop::text[]) w(t)
                  CROSS JOIN LATERAL laplace.prompt_state(w.t) p WHERE p.id IS NOT NULL),
        topic AS (SELECT id FROM laplace.prompt_state(@prompt) WHERE id IS NOT NULL),
        field AS (
            SELECT id FROM topic
            UNION SELECT c.object_id FROM laplace.consensus c JOIN topic t ON c.subject_id=t.id
                  WHERE c.type_id=laplace.relation_type_id('PRECEDES') AND NOT laplace.refuted(c.rating,c.rd)
            UNION SELECT c.subject_id FROM laplace.consensus c JOIN topic t ON c.object_id=t.id
                  WHERE c.type_id=laplace.relation_type_id('PRECEDES') AND NOT laplace.refuted(c.rating,c.rd)
            UNION SELECT c.object_id FROM laplace.consensus c JOIN topic t ON c.subject_id=t.id
                  WHERE c.type_id=laplace.relation_type_id('IS_SYNONYM_OF')),
        seed AS (SELECT array_agg(id ORDER BY ord) ctx FROM laplace.prompt_state(@prompt) WHERE id IS NOT NULL),
        walk AS (
            SELECT s.ctx AS ctx, NULL::bytea oid, NULL::numeric mu, 0 AS step FROM seed s WHERE s.ctx IS NOT NULL
            UNION ALL
            SELECT w.ctx || nx.oid, nx.oid, nx.mu, w.step + 1
            FROM walk w CROSS JOIN LATERAL (
                SELECT oid, mu FROM (
                    SELECT c.object_id oid, laplace.eff_mu_display(max(c.rating),max(c.rd)) mu,
                           sum(laplace.eff_mu(c.rating,c.rd))/1e9
                             * (CASE WHEN @boost>0 AND c.object_id IN (SELECT id FROM field) THEN 1+@boost ELSE 1 END) AS sc
                    FROM laplace.consensus c
                    WHERE c.type_id = laplace.relation_type_id('PRECEDES')
                      AND c.subject_id = ANY (w.ctx[GREATEST(1,array_length(w.ctx,1)-@window+1):array_length(w.ctx,1)])
                      AND c.object_id IS NOT NULL AND NOT laplace.refuted(c.rating,c.rd)
                      AND c.object_id <> ALL (w.ctx) AND c.object_id NOT IN (SELECT id FROM stops)
                      AND EXISTS (SELECT 1 FROM laplace.consensus h
                                  WHERE h.subject_id=c.object_id AND h.type_id=laplace.relation_type_id('HAS_POS'))
                    GROUP BY c.object_id ORDER BY sc DESC LIMIT @topk
                ) cand
                ORDER BY CASE WHEN @temp<=0 THEN sc
                              ELSE power(random(),1.0/GREATEST(power(sc,1.0/@temp),1e-9)) END DESC
                LIMIT 1
            ) nx WHERE w.step < @steps
        )
        SELECT step, laplace.render_text(oid,24) AS token, mu FROM walk WHERE step>0 ORDER BY step";

    private static async Task<int> GenerateAsync(string[] args)
    {
        int steps  = EnvInt("LAPLACE_GEN_STEPS", 20, 1);
        int window = EnvInt("LAPLACE_GEN_WINDOW", 3, 1);
        int topk   = EnvInt("LAPLACE_GEN_TOPK", 8, 1);
        double temp = EnvDouble("LAPLACE_GEN_TEMP", 0.6);
        double steer = EnvDouble("LAPLACE_GEN_STEER", 0.0);
        bool verbose = Environment.GetEnvironmentVariable("LAPLACE_GEN_VERBOSE") == "1";

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();

        string prompt = string.Join(' ', args).Trim();
        if (!string.IsNullOrWhiteSpace(prompt))
            return await GenerateOnceAsync(conn, prompt, steps, window, temp, topk, steer, verbose);

        Console.WriteLine("laplace generate — type a prompt, Enter. Blank line or Ctrl-D quits.");
        while (true)
        {
            Console.Write("\nprompt> ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            await GenerateOnceAsync(conn, line, steps, window, temp, topk, steer, verbose);
        }
        return 0;
    }

    private static async Task<int> GenerateOnceAsync(
        NpgsqlConnection conn, string prompt, int steps, int window, double temp, int topk, double boost, bool verbose)
    {
        var sw = Stopwatch.StartNew();
        var toks = new List<(string tok, decimal mu)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = GenerateSql;
            cmd.Parameters.AddWithValue("prompt", prompt);
            cmd.Parameters.AddWithValue("stop", GenStop);
            cmd.Parameters.AddWithValue("steps", steps);
            cmd.Parameters.AddWithValue("window", window);
            cmd.Parameters.AddWithValue("temp", temp);
            cmd.Parameters.AddWithValue("topk", topk);
            cmd.Parameters.AddWithValue("boost", boost);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                toks.Add((r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? 0m : r.GetDecimal(2)));
        }
        sw.Stop();
        Console.WriteLine(prompt + " " + string.Join(' ', toks.Select(t => t.tok)));
        if (verbose)
            for (int i = 0; i < toks.Count; i++)
                Console.WriteLine($"    {i + 1,2}. {toks[i].tok,-22} μ={toks[i].mu:F1}");
        Console.WriteLine($"    [{toks.Count} tokens, {sw.Elapsed.TotalMilliseconds:F0} ms — ranked-μ walk, no GPU]");
        return 0;
    }

    private static async Task<int> InspectAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return Fail("usage: laplace inspect <text>");
        CodepointPerfcache.Load(ResolveBlob());

        using var tree = TextDecomposer.Run(text);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

        var root = tree.GetNode(tree.NaturalUnitIndex());
        Hash128 id = root.Id;
        Console.WriteLine($"inspect \"{text}\"");
        Console.WriteLine($"  engine-resolved id : {Hex(id)}");
        Console.WriteLine($"  tier {root.Tier}, {tree.NodeCount} nodes in the decomposition DAG\n");

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();

        bool exists = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT f.tier, laplace.render(f.type_id) FROM laplace.entity_facets(@id) f";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                exists = true;
                Console.WriteLine($"  ENTITY: present  tier={r.GetInt16(0)}  type={r.GetString(1)}");
            }
        }
        var utf8Input = Encoding.UTF8.GetBytes(text);
        var wordSeen  = new HashSet<Hash128>();
        var words     = new List<(Hash128 Id, string Label)>();
        for (uint i = 0; i < (uint)tree.NodeCount; i++)
        {
            var v = tree.GetNode(i);
            if (v.Tier != 2) continue;
            if (!wordSeen.Add(v.Id)) continue;
            words.Add((v.Id, Encoding.UTF8.GetString(utf8Input, (int)v.TextRangeOff, (int)v.TextRangeLen)));
        }

        if (!exists)
            Console.WriteLine("  ENTITY: novel composite — correct id, not yet ingested (a prompt ingest binds it)");

        if (words.Count > 1 || !exists)
        {
            Console.WriteLine("\n  CONSTITUENT KNOWLEDGE (the substrate answering through the parts it knows):");
            foreach (var (wid, label) in words)
            {
                await using var wc = conn.CreateCommand();
                wc.CommandText = "SELECT type, object, eff_mu, witnesses "
                               + "FROM laplace.consensus_out_readable(@id, 2)";
                wc.Parameters.AddWithValue("id", wid.ToBytes());
                await using var wr = await wc.ExecuteReaderAsync();
                bool any = false;
                while (await wr.ReadAsync())
                {
                    if (!any) Console.WriteLine($"    \"{label}\"");
                    any = true;
                    string obj = wr.IsDBNull(1) ? "(unary)" : wr.GetString(1);
                    Console.WriteLine($"        [{wr.GetString(0)}] → {obj}  μ={wr.GetDecimal(2):F3}  witnesses={wr.GetInt64(3)}");
                }
                if (!any) Console.WriteLine($"    \"{label}\"  (no consensus yet)");
            }
        }

        if (!exists) return 0;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT p.type, p.x, p.y, p.z, p.m, p.radius, p.n_constituents, laplace.render(p.source_id) "
                            + "FROM laplace.entity_physicalities(@id) p";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            Console.WriteLine("\n  GLOME (physicalities):");
            int n = 0;
            while (await r.ReadAsync())
            {
                n++;
                Console.WriteLine($"    kind={r.GetInt16(0)}  coord=({r.GetDouble(1):F4},{r.GetDouble(2):F4},{r.GetDouble(3):F4},{r.GetDouble(4):F4})"
                    + $"  r={r.GetDouble(5):F6}  n_constituents={r.GetInt32(6)}  source={r.GetString(7)}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT laplace.render(c.type_id), laplace.render(c.object_id), "
                            + "c.rating, c.rd, c.volatility, c.witness_count "
                            + "FROM laplace.consensus_out(@id) c";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            Console.WriteLine("\n  OUTGOING consensus (this → object), Glicko-2 μ over all witnesses:");
            int n = 0;
            while (await r.ReadAsync())
            {
                n++;
                string kind = r.GetString(0);
                string obj  = r.IsDBNull(1) ? "(unary)" : r.GetString(1);
                Console.WriteLine($"    [{kind}] → {obj,-28}  μ={r.GetInt64(2)/1e9:F3} rd={r.GetInt64(3)/1e9:F3} σ={r.GetInt64(4)/1e9:F4}"
                    + $"  witnesses={r.GetInt64(5)}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT laplace.render(c.subject_id), laplace.render(c.type_id), "
                            + "c.rating, c.rd, c.volatility, c.witness_count "
                            + "FROM laplace.consensus_in(@id) c";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            Console.WriteLine("\n  INCOMING consensus (subject → this), Glicko-2 μ over all witnesses:");
            int n = 0;
            while (await r.ReadAsync())
            {
                n++;
                Console.WriteLine($"    {r.GetString(0),-28} [{r.GetString(1)}] → here  μ={r.GetInt64(2)/1e9:F3} rd={r.GetInt64(3)/1e9:F3} σ={r.GetInt64(4)/1e9:F4}"
                    + $"  witnesses={r.GetInt64(5)}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        static string Outc(short o) => o switch { 0 => "refute", 1 => "draw", _ => "confirm" };
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT laplace.render(a.type_id), laplace.render(a.object_id), "
                            + "laplace.render(a.source_id), a.context_id, a.outcome, a.observation_count "
                            + "FROM laplace.attestations_out(@id) a";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            Console.WriteLine("\n  OUTGOING evidence (provenance — who witnessed):");
            int n = 0;
            while (await r.ReadAsync())
            {
                n++;
                string obj = r.IsDBNull(1) ? "(unary)" : r.GetString(1);
                string ctx = r.IsDBNull(3) ? "-" : Hex(ReadHash16((byte[])r[3]))[..10] + "…";
                Console.WriteLine($"    [{r.GetString(0)}] → {obj,-28}  {Outc(r.GetInt16(4))}"
                    + $"  src={r.GetString(2)}  ctx={ctx}  games={r.GetInt64(5)}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT laplace.render(a.subject_id), laplace.render(a.type_id), "
                            + "laplace.render(a.source_id), a.context_id, a.outcome, a.observation_count "
                            + "FROM laplace.attestations_in(@id) a";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            Console.WriteLine("\n  INCOMING evidence (provenance — who witnessed):");
            int n = 0;
            while (await r.ReadAsync())
            {
                n++;
                string ctx = r.IsDBNull(3) ? "-" : Hex(ReadHash16((byte[])r[3]))[..10] + "…";
                Console.WriteLine($"    {r.GetString(0),-28} [{r.GetString(1)}] → here  {Outc(r.GetInt16(4))}"
                    + $"  src={r.GetString(2)}  ctx={ctx}  games={r.GetInt64(5)}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        return 0;
    }

    private static async Task<int> IngestAsync(string[] args)
    {
        string source = args.Length > 0 ? args[0] : "";
        string path   = args.Length > 1 ? args[1] : "";

        if (string.IsNullOrEmpty(source))
            return Fail("usage: laplace ingest <source> [path]  (unicode | iso639 | wordnet | omw | ud | tatoeba | atomic2020 | conceptnet | wiktionary | framenet | opensubtitles | verbnet | propbank | semlink | model)");

        return source.ToLowerInvariant() switch
        {
            "unicode"  => await IngestUnicodeViaRunnerAsync(),
            "iso639"   => await IngestISO639Async(),
            "wordnet"  => await IngestViaRunnerAsync(new WordNetDecomposer(), "/vault/Data/Wordnet", skipLayerCheck: false),
            "omw"      => await IngestViaRunnerAsync(new OMWDecomposer(), "/vault/Data/omw", skipLayerCheck: false),
            "ud"       => await IngestViaRunnerAsync(new UDDecomposer(), "/vault/Data/UD-Treebanks", skipLayerCheck: false),
            "tatoeba"  => await IngestViaRunnerAsync(new TatoebaDecomposer(), "/vault/Data/Tatoeba", skipLayerCheck: false),
            "atomic2020" => await IngestViaRunnerAsync(new Atomic2020Decomposer(), "/vault/Data/Atomic2020", skipLayerCheck: false),
            "conceptnet" => await IngestViaRunnerAsync(new ConceptNetDecomposer(), "/vault/Data/ConceptNet", skipLayerCheck: false),
            "wiktionary" => await IngestViaRunnerAsync(new WiktionaryDecomposer(), "/vault/Data/Wiktionary", skipLayerCheck: false),
            "framenet" => await IngestViaRunnerAsync(new FrameNetDecomposer(), "/vault/Data/FrameNet/framenet_v17", skipLayerCheck: false),
            "opensubtitles" => await IngestViaRunnerAsync(new OpenSubtitlesDecomposer(), "/vault/Data/OpenSubtitles", skipLayerCheck: false),
            "verbnet"  => await IngestViaRunnerAsync(new VerbNetDecomposer(),  "/vault/Data/VerbNet",  skipLayerCheck: false),
            "propbank" => await IngestViaRunnerAsync(new PropBankDecomposer(), "/vault/Data/PropBank", skipLayerCheck: false),
            "semlink"  => await IngestViaRunnerAsync(new SemLinkDecomposer(),  "/vault/Data/SemLink",  skipLayerCheck: false),
            "model"    => await IngestModelAsync(path),
            _ => Fail($"unknown ingest source '{source}' (supported: unicode, iso639, wordnet, omw, ud, tatoeba, atomic2020, conceptnet, wiktionary, framenet, opensubtitles, verbnet, propbank, semlink, model)"),
        };
    }

    private static async Task<int> IngestModelAsync(string modelDir)
    {
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
            return Fail($"usage: laplace ingest model <model-dir>  (not found: {modelDir})");

        CodepointPerfcache.Load(ResolveBlob());

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();

        var dec = new ModelDecomposer(modelDir);

        var (modelSource, modelName) = ModelDecomposer.SourceForModel(modelDir);
        await using (var chkConn = await ds.OpenConnectionAsync())
        {
            await using var chkCmd = chkConn.CreateCommand();
            chkCmd.CommandText =
                "SELECT laplace.evidence_count(p_type => $2, p_source => $1) > 0";
            chkCmd.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = modelSource.ToBytes(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
            chkCmd.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = Laplace.Ingestion.LayerCompletion.RelationTypeId(dec.LayerOrder).ToBytes(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
            bool alreadyIngested = (bool)(await chkCmd.ExecuteScalarAsync() ?? false);
            if (alreadyIngested)
            {
                Console.WriteLine($"Model already ingested — source {modelName}: {modelSource}");
                Console.WriteLine($"(re-ingest is refused to prevent consensus contamination; "
                                  + $"reset with 'just db-fresh' to test from scratch)");
                return 0;
            }
        }

        var loggerFactory = ConsoleLoggerProvider.Factory();
        var inner = new NpgsqlSubstrateWriter(ds);
        var accumulator = new ConsensusAccumulatingWriter(inner, ds,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        ISubstrateWriter writer = accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory);
        Console.WriteLine("mode: consensus accumulates at ingest; evidence = provenance-only rows");

        Console.WriteLine($"ingest model {modelDir} via IngestRunner → {ConnString} ...");
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            dec,
            BuildIngestOptions(sw, dec.SourceName, skipLayerCheck: true, ecosystemPath: null),
            CancellationToken.None);
        sw.Stop();

        Console.WriteLine(
            $"done: {result.UnitsApplied:N0} intents applied, "
            + $"{result.EntitiesInserted:N0} novel entities, "
            + $"{result.AttestationsInserted:N0} attestations, "
            + $"{result.TotalRoundTrips:N0} round-trips, "
            + $"{sw.Elapsed.TotalSeconds:F1}s");
        if (result.Failures.Count > 0)
        {
            Console.Error.WriteLine($"failures: {result.Failures.Count}");
            foreach (var f in result.Failures.Take(5))
                Console.Error.WriteLine($"  {f}");
            return 1;
        }
        {
            Console.WriteLine(
                $"consensus: folding {accumulator.ObservationsAccumulated:N0} matches "
                + $"across {accumulator.FoldWorkers} partition(s) ...");
            var matSw = Stopwatch.StartNew();
            var materialized = await accumulator.MaterializeConsensusAsync();
            matSw.Stop();
            Console.WriteLine(
                $"consensus: {materialized:N0} relations materialized from "
                + $"{accumulator.ObservationsAccumulated:N0} matches in {matSw.Elapsed.TotalSeconds:F1}s "
                + $"(accumulated at ingest; evidence = provenance-only)");
        }
        await RegisterDynamicCanonicalsAsync(ds, ((IDecomposer)dec).CanonicalNamesForReadback);
        return 0;
    }

    private static async Task<int> SynthesizeAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (sub == "substrate")
        {
            string recipePath = args.Length > 1 ? args[1] : "";
            string? outEnv = Environment.GetEnvironmentVariable("LAPLACE_GGUF_OUT");
            string outputPath = args.Length > 2 ? args[2]
                : !string.IsNullOrEmpty(outEnv) ? outEnv
                : "";
            if (string.IsNullOrEmpty(outputPath))
                return Fail("usage: laplace synthesize substrate <recipe.json> <output.gguf>\n"
                          + "  (or set LAPLACE_GGUF_OUT; no temp-dir default)");
            return await SynthesizeFromSubstrateAsync(recipePath, outputPath);
        }

        return Fail(
            "usage: laplace synthesize <subcommand> [args]\n"
            + "  substrate <recipe.json> [output.gguf]   substrate-mediated synthesis\n");
    }

    private static async Task<int> SynthesizeFromSubstrateAsync(string recipePath, string outputPath)
    {
        if (string.IsNullOrEmpty(recipePath) || !File.Exists(recipePath))
            return Fail(
                "usage: laplace synthesize substrate <recipe.json> [output.gguf]\n"
                + $"  (recipe not found: {recipePath})");

        Console.WriteLine($"synthesize substrate (Stream B-minimum) → {outputPath}");
        CodepointPerfcache.Load(ResolveBlob());

        string modelDir = Path.GetDirectoryName(recipePath) ?? ".";
        string tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(tokenizerPath))
            return Fail($"tokenizer.json not found alongside recipe: {tokenizerPath}");

        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        var recipe = LlamaRecipeExtractor.Parse(recipePath);
        int vocab = recipe.VocabSize;
        int dModel = recipe.HiddenSize;

        var tokenSlots = new Dictionary<Hash128, List<int>>(tokens.Count);
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab) continue;
            if (!tokenSlots.TryGetValue(t.EntityId, out var slots))
                tokenSlots[t.EntityId] = slots = new List<int>(1);
            slots.Add(t.TokenId);
        }
        var (moldSource, moldSourceName) = ModelDecomposer.SourceForModel(modelDir);
        Console.WriteLine($"  mold source: {moldSourceName} ({moldSource})");
        int nHeadsR = recipe.NumHeads, nKvR = recipe.NumKvHeads;
        int headDimR = dModel / Math.Max(1, nHeadsR);
        int attnOutR = nHeadsR * headDimR, kvDimR = nKvR * headDimR;
        int intermR  = recipe.IntermediateSize;
        Dictionary<Hash128, int[]> AxisMap(string space, int dim)
        {
            var m = new Dictionary<Hash128, int[]>(dim);
            for (int i = 0; i < dim; i++)
                m[SourceEntityIdConventions.ModelAxisEntity(moldSource, space, i)] = [i];
            return m;
        }
        var chanMap   = AxisMap("channel",  dModel);
        var attnMap   = AxisMap("attn_dim", attnOutR);
        var kvMap     = AxisMap("kv_dim",   kvDimR);
        var neuronMap = AxisMap("neuron",   intermR);
        Func<Hash128, IReadOnlyList<int>?> Tok   = e => tokenSlots.TryGetValue(e, out var s) ? s : null;
        Func<Hash128, IReadOnlyList<int>?> Of(Dictionary<Hash128, int[]> m) =>
            e => m.TryGetValue(e, out var s) ? s : null;

        byte[] configJson = File.ReadAllBytes(recipePath);
        IntPtr recipeHandle, tmplHandle;
        var specs = new TensorSpec[300];
        int tensorCount;
        unsafe
        {
            fixed (byte* jp = configJson) recipeHandle = SynthInterop.RecipeParse(jp, (nuint)configJson.Length);
            if (recipeHandle == IntPtr.Zero) return Fail("recipe_parse returned null");
            tmplHandle = SynthInterop.ArchTemplateLoad("llama");
            if (tmplHandle == IntPtr.Zero) return Fail("arch_template_load returned null");
            fixed (TensorSpec* sp = specs)
                tensorCount = SynthInterop.ArchTemplateRequiredTensors(tmplHandle, recipeHandle, sp, (nuint)specs.Length);
        }
        if (tensorCount <= 0) return Fail($"arch_template_required_tensors returned {tensorCount}");
        Console.WriteLine($"  recipe + arch template: {tensorCount} tensor slots, vocab={vocab}, hidden={dModel}");

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();

        Dictionary<string, SafetensorsContainerParser.TensorReference>? refMap = null;
        try
        {
            var refs = SafetensorsContainerParser.ParseModel(modelDir);
            if (refs.Count > 0)
            {
                refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
                    refs.Count, StringComparer.Ordinal);
                foreach (var r in refs) refMap[r.Name] = r;
            }
        }
        catch { }
        if (refMap is null)
            Console.WriteLine("  (no reference tensors alongside the recipe — per-role scale M = 1.0)");

        var prof = ArchitectureProfile.For(recipe.ModelType);
        IEnumerable<string> Layers(string tpl) =>
            Enumerable.Range(0, recipe.NumLayers).Select(l => ArchitectureProfile.Layer(tpl, l));
        double MOf(params IEnumerable<string>[] groups) =>
            ConsensusReExport.MoldArenaScale(refMap, groups.SelectMany(g => g));

        Console.WriteLine("  pouring consensus arenas into the mold's tensor layouts...");
        var swArena = Stopwatch.StartNew();
        var chan = Of(chanMap); var attn = Of(attnMap); var kv = Of(kvMap); var neuron = Of(neuronMap);
        var embedA = await ConsensusReExport.ReadTableArenaAsync(ds, ModelDecomposer.EmbedsTypeId,
            vocab, dModel, rowsAreOut: false, Tok, chan, MOf([prof.EmbedTokens]));
        var lmHeadA = await ConsensusReExport.ReadTableArenaAsync(ds, ModelDecomposer.OutputProjectsTypeId,
            vocab, dModel, rowsAreOut: true, chan, Tok, MOf([prof.LmHead ?? prof.EmbedTokens]));
        var qA = await ConsensusReExport.ReadTableArenaAsync(ds, ModelDecomposer.QProjectsTypeId,
            attnOutR, dModel, rowsAreOut: true, chan, attn, MOf(Layers(prof.QProj)));
        var kA = await ConsensusReExport.ReadTableArenaAsync(ds, ModelDecomposer.KProjectsTypeId,
            kvDimR, dModel, rowsAreOut: true, chan, kv, MOf(Layers(prof.KProj)));
        var vA = await ConsensusReExport.ReadTableArenaAsync(ds, ModelDecomposer.VProjectsTypeId,
            kvDimR, dModel, rowsAreOut: true, chan, kv, MOf(Layers(prof.VProj)));
        var oA = await ConsensusReExport.ReadTableArenaAsync(ds, ModelDecomposer.OProjectsTypeId,
            dModel, attnOutR, rowsAreOut: true, attn, chan, MOf(Layers(prof.OProj)));
        var gateA = prof.GateProj is null ? null
            : await ConsensusReExport.ReadTableArenaAsync(ds, ModelDecomposer.GatesTypeId,
                intermR, dModel, rowsAreOut: true, chan, neuron, MOf(Layers(prof.GateProj)));
        var upA = await ConsensusReExport.ReadTableArenaAsync(ds, ModelDecomposer.UpProjectsTypeId,
            intermR, dModel, rowsAreOut: true, chan, neuron, MOf(Layers(prof.UpProj)));
        var downA = await ConsensusReExport.ReadTableArenaAsync(ds, ModelDecomposer.DownProjectsTypeId,
            dModel, intermR, rowsAreOut: true, neuron, chan, MOf(Layers(prof.DownProj)));
        var normV = await ConsensusReExport.ReadNormVectorAsync(ds, ModelDecomposer.NormScalesTypeId,
            dModel, chan, MOf(Layers(prof.PerLayerNorms[0]), [prof.FinalNorm]));

        long totalRelations = embedA.Relations + lmHeadA.Relations + qA.Relations + kA.Relations
            + vA.Relations + oA.Relations + (gateA?.Relations ?? 0) + upA.Relations + downA.Relations;
        Console.WriteLine(
            $"  consensus arenas poured in {swArena.Elapsed.TotalSeconds:F1}s: EMBEDS={embedA.Relations:N0} "
            + $"OUTPUT_PROJECTS={lmHeadA.Relations:N0} Q={qA.Relations:N0} K={kA.Relations:N0} V={vA.Relations:N0} "
            + $"O={oA.Relations:N0} GATES={gateA?.Relations ?? 0:N0} UP={upA.Relations:N0} DOWN={downA.Relations:N0}");
        if (totalRelations == 0)
            return Fail("no model consensus in the substrate — ingest a model first");

        var gguf = SynthInterop.GgufWriterCreate(outputPath);
        if (gguf == IntPtr.Zero) return Fail($"gguf_writer_create failed for {outputPath}");
        WriteGgufMetadata(gguf, recipe, tokens, modelDir);

        var sw = Stopwatch.StartNew();
        int tensorsDone = 0;

        for (int i = 0; i < tensorCount; i++)
        {
            string name; ulong rows, cols; int dtype;
            unsafe
            {
                var sp = specs[i];
                name  = Marshal.PtrToStringUTF8((IntPtr)sp.Name) ?? "";
                rows  = sp.Rank >= 1 ? sp.Shape[0] : 1;
                cols  = sp.Rank >= 2 ? sp.Shape[1] : 1;
                dtype = sp.Dtype;
            }
            long nElem = (long)rows * (long)Math.Max(1UL, cols);
            var vals = new float[nElem];

            FillMoldTensor(vals, name,
                embedA, lmHeadA, qA, kA, vA, oA, gateA, upA, downA, normV);

            byte[] tensorBytes = dtype == 0
                ? ConsensusReExport.ToF32Bytes(vals)
                : ConsensusReExport.ToBf16Bytes(vals);

            nuint[] ggufDims = cols > 1 ? [(nuint)cols, (nuint)rows] : [(nuint)rows];
            unsafe
            {
                fixed (nuint* dimsPtr = ggufDims)
                fixed (byte*  dataPtr = tensorBytes)
                    SynthInterop.GgufWriterAddTensor(gguf, HfToGgmlName(name), dtype, dimsPtr, (nuint)ggufDims.Length, dataPtr);
            }

            tensorsDone++;
            if (tensorsDone == 1 || tensorsDone % 20 == 0)
                Console.WriteLine($"  [{tensorsDone}/{tensorCount}] {name} rows={rows} cols={cols} {sw.Elapsed.TotalSeconds:F1}s");
        }

        int rc = SynthInterop.GgufWriterFinalize(gguf);
        SynthInterop.GgufWriterFree(gguf);
        SynthInterop.ArchTemplateFree(tmplHandle);
        SynthInterop.RecipeFree(recipeHandle);
        if (rc != 0) return Fail($"gguf_writer_finalize failed (rc={rc})");

        long fileSize = new FileInfo(outputPath).Length;
        Console.WriteLine($"synthesis complete: {outputPath} ({fileSize / 1048576.0:F0} MB) in {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static void FillMoldTensor(
        float[] vals, string name,
        ConsensusReExport.TableArena embedA, ConsensusReExport.TableArena lmHeadA,
        ConsensusReExport.TableArena qA, ConsensusReExport.TableArena kA,
        ConsensusReExport.TableArena vA, ConsensusReExport.TableArena oA,
        ConsensusReExport.TableArena? gateA, ConsensusReExport.TableArena upA,
        ConsensusReExport.TableArena downA, float[] normV)
    {
        ConsensusReExport.TableArena? arena =
              name == "model.embed_tokens.weight" ? embedA
            : name == "lm_head.weight"            ? lmHeadA
            : name.EndsWith(".self_attn.q_proj.weight", StringComparison.Ordinal) ? qA
            : name.EndsWith(".self_attn.k_proj.weight", StringComparison.Ordinal) ? kA
            : name.EndsWith(".self_attn.v_proj.weight", StringComparison.Ordinal) ? vA
            : name.EndsWith(".self_attn.o_proj.weight", StringComparison.Ordinal) ? oA
            : name.EndsWith(".mlp.gate_proj.weight",    StringComparison.Ordinal) ? gateA
            : name.EndsWith(".mlp.up_proj.weight",      StringComparison.Ordinal) ? upA
            : name.EndsWith(".mlp.down_proj.weight",    StringComparison.Ordinal) ? downA
            : null;

        if (arena is not null)
        {
            if (arena.Cells.LongLength != vals.LongLength)
                throw new InvalidOperationException(
                    $"mold slot {name} has {vals.LongLength:N0} cells but the arena's schema shape is "
                    + $"[{arena.Rows}×{arena.Cols}] = {arena.Cells.LongLength:N0} — shape/rank retargeting "
                    + "(export-only SVD) is not built; the mold must match the substrate's schema shape");
            Array.Copy(arena.Cells, vals, vals.Length);
            return;
        }
        if (name.EndsWith("norm.weight", StringComparison.Ordinal))
        {
            if (normV.Length != vals.Length)
                throw new InvalidOperationException(
                    $"mold norm slot {name} has {vals.Length:N0} channels but NORM_SCALES carries {normV.Length:N0}");
            Array.Copy(normV, vals, vals.Length);
            return;
        }
    }


    private static string HfToGgmlName(string hf)
    {
        if (hf == "model.embed_tokens.weight") return "token_embd.weight";
        if (hf == "model.norm.weight")         return "output_norm.weight";
        if (hf == "lm_head.weight")            return "output.weight";

        const string prefix = "model.layers.";
        if (hf.StartsWith(prefix, StringComparison.Ordinal))
        {
            int dot = hf.IndexOf('.', prefix.Length);
            if (dot > 0)
            {
                string idx  = hf.Substring(prefix.Length, dot - prefix.Length);
                string rest = hf.Substring(dot + 1);
                string g = rest switch
                {
                    "self_attn.q_proj.weight"          => "attn_q.weight",
                    "self_attn.k_proj.weight"          => "attn_k.weight",
                    "self_attn.v_proj.weight"          => "attn_v.weight",
                    "self_attn.o_proj.weight"          => "attn_output.weight",
                    "mlp.gate_proj.weight"             => "ffn_gate.weight",
                    "mlp.up_proj.weight"               => "ffn_up.weight",
                    "mlp.down_proj.weight"             => "ffn_down.weight",
                    "input_layernorm.weight"           => "attn_norm.weight",
                    "post_attention_layernorm.weight"  => "ffn_norm.weight",
                    _                                  => rest,
                };
                return $"blk.{idx}.{g}";
            }
        }
        return hf;
    }

    private static unsafe Hash128 Hash128FromBytes(byte[] b)
    {
        if (b.Length < 16) return Hash128.Zero;
        fixed (byte* p = b) return *(Hash128*)p;
    }

    private static void WriteGgufMetadata(
        IntPtr gguf,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        string modelDir)
    {
        SynthInterop.GgufWriterAddMetadataStr(gguf, "general.architecture", "llama");
        SynthInterop.GgufWriterAddMetadataStr(gguf, "general.name", Path.GetFileName(modelDir.TrimEnd('/')));

        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.context_length",          2048);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.embedding_length",         (uint)recipe.HiddenSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.block_count",              (uint)recipe.NumLayers);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.feed_forward_length",      (uint)recipe.IntermediateSize);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count",     (uint)recipe.NumHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.attention.head_count_kv",  (uint)recipe.NumKvHeads);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "llama.vocab_size",               (uint)recipe.VocabSize);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.attention.layer_norm_rms_epsilon", 1e-5f);
        SynthInterop.GgufWriterAddMetadataF32(gguf, "llama.rope.freq_base",           (float)recipe.RopeTheta);

        SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.ggml.model", "llama");
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.bos_token_id",     1);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.eos_token_id",     2);
        SynthInterop.GgufWriterAddMetadataU32(gguf, "tokenizer.ggml.unknown_token_id", 0);
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_bos_token",    1);
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_eos_token",    0);
        SynthInterop.GgufWriterAddMetadataBool(gguf, "tokenizer.ggml.add_space_prefix", 1);

        int n = tokens.Count;

        string spPath = Path.Combine(modelDir, "tokenizer.model");
        SpPiece[]? sp = File.Exists(spPath) ? ParseSentencePieceModel(spPath) : null;

        string[] pieces = new string[n];
        float[]  scores = new float[n];
        int[]    types  = new int[n];

        if (sp is not null && sp.Length == n)
        {
            for (int i = 0; i < n; i++) { pieces[i] = sp[i].Piece; scores[i] = sp[i].Score; types[i] = sp[i].Type; }
        }
        else
        {
            Console.WriteLine($"  WARN: tokenizer.model {(sp is null ? "missing" : $"has {sp.Length} pieces ≠ vocab {n}")} — "
                + "falling back to tokenizer.json strings + zero scores (tokenization will be degraded)");
            var sorted = tokens.OrderBy(t => t.TokenId).ToArray();
            for (int i = 0; i < n; i++) { pieces[i] = sorted[i].RawToken; scores[i] = 0f; types[i] = ClassifyTokenType(sorted[i].RawToken); }
        }

        byte[] packed = PackStrings(pieces);
        unsafe
        {
            fixed (byte* p = packed)
                SynthInterop.GgufWriterAddMetadataStrArrayPacked(
                    gguf, "tokenizer.ggml.tokens", p, (nuint)packed.Length, (nuint)n);
            fixed (float* p = scores)
                SynthInterop.GgufWriterAddMetadataF32Array(gguf, "tokenizer.ggml.scores", p, (nuint)n);
            fixed (int* p = types)
                SynthInterop.GgufWriterAddMetadataI32Array(gguf, "tokenizer.ggml.token_type", p, (nuint)n);
        }

        string cfgPath = Path.Combine(modelDir, "tokenizer_config.json");
        if (File.Exists(cfgPath))
        {
            using var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(cfgPath));
            if (cfg.RootElement.TryGetProperty("chat_template", out var ct)
                && ct.ValueKind == System.Text.Json.JsonValueKind.String)
                SynthInterop.GgufWriterAddMetadataStr(gguf, "tokenizer.chat_template", ct.GetString()!);
        }
    }

    private sealed record SpPiece(string Piece, float Score, int Type);

    private static SpPiece[] ParseSentencePieceModel(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        var pieces = new List<SpPiece>(32000);
        int pos = 0;
        while (pos < d.Length)
        {
            ulong key = ReadVarint(d, ref pos);
            int field = (int)(key >> 3), wt = (int)(key & 7);
            if (field == 1 && wt == 2)
            {
                int len = (int)ReadVarint(d, ref pos);
                int end = pos + len;
                string piece = ""; float score = 0f; int type = 1;
                while (pos < end)
                {
                    ulong k2 = ReadVarint(d, ref pos);
                    int f2 = (int)(k2 >> 3), w2 = (int)(k2 & 7);
                    if      (f2 == 1 && w2 == 2) { int l = (int)ReadVarint(d, ref pos); piece = Encoding.UTF8.GetString(d, pos, l); pos += l; }
                    else if (f2 == 2 && w2 == 5) { score = BitConverter.ToSingle(d, pos); pos += 4; }
                    else if (f2 == 3 && w2 == 0) { type = (int)ReadVarint(d, ref pos); }
                    else SkipField(d, ref pos, w2);
                }
                pieces.Add(new SpPiece(piece, score, type));
                pos = end;
            }
            else SkipField(d, ref pos, wt);
        }
        return pieces.ToArray();
    }

    private static ulong ReadVarint(byte[] d, ref int pos)
    {
        ulong v = 0; int shift = 0;
        while (pos < d.Length)
        {
            byte b = d[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }

    private static void SkipField(byte[] d, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(d, ref pos); break;
            case 1: pos += 8; break;
            case 2: { int l = (int)ReadVarint(d, ref pos); pos += l; break; }
            case 5: pos += 4; break;
            default: throw new InvalidDataException($"SP proto: unsupported wire type {wireType}");
        }
    }

    private static byte[] PackStrings(IReadOnlyList<string> strings)
    {
        using var ms = new System.IO.MemoryStream();
        Span<byte> lenBuf = stackalloc byte[8];
        foreach (var s in strings)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)b.Length);
            ms.Write(lenBuf);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    private static int ClassifyTokenType(string raw)
    {
        if (raw is "<unk>" or "<UNK>" or "<unknown>") return 1;
        if (raw is "<s>" or "</s>" or "<pad>" or "<bos>" or "<eos>") return 2;
        if (raw.Length == 6 && raw.StartsWith("<0x", StringComparison.Ordinal) && raw.EndsWith('>')) return 5;
        return 0;
    }

    private static async Task<int> IngestUnicodeViaRunnerAsync()
        => await IngestViaRunnerAsync(new UnicodeDecomposer(), "/vault/Data/Unicode", skipLayerCheck: true);

    private static async Task<int> IngestISO639Async()
        => await IngestViaRunnerAsync(new ISODecomposer(), "/vault/Data/ISO639", skipLayerCheck: false);

    private static IngestRunOptions BuildIngestOptions(
        Stopwatch sw, string sourceName, bool skipLayerCheck, string? ecosystemPath)
    {
        long lastMs = -10_000;
        var progress = new Progress<Laplace.Ingestion.IngestProgress>(p =>
        {
            long now = sw.ElapsedMilliseconds;
            if (now - lastMs < 2000) return;
            lastMs = now;
            double secs = Math.Max(0.001, p.Elapsed.TotalSeconds);
            long rows = p.EntitiesInserted + p.PhysicalitiesInserted + p.AttestationsInserted;
            Console.Error.WriteLine(
                $"[{sourceName}] recorded {rows:N0} rows = {p.EntitiesInserted:N0} ent + "
                + $"{p.PhysicalitiesInserted:N0} phys + {p.AttestationsInserted:N0} att "
                + $"@ {rows / secs:N0} rows/s; {p.UnitsApplied:N0}/{p.UnitsProduced:N0} intents applied/decomposed "
                + $"({p.UnitsProduced / secs:N1} dec/s); {p.RoundTrips:N0} RT"
                + (p.EstimatedTotal is { } t && t > 0 ? $"; ~{100.0 * p.UnitsProduced / t:F0}% of {t:N0} units" : "")
                + $"; {p.Elapsed.TotalSeconds:F0}s"
                + (p.UnitsFailed > 0 ? $"; {p.UnitsFailed:N0} FAILED" : ""));
        });
        return IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = skipLayerCheck,
            EcosystemPath          = ecosystemPath,
            BatchSize              = EnvInt("LAPLACE_INGEST_BATCH",       1024,    min: 1),
            CommitRows             = EnvInt("LAPLACE_INGEST_COMMIT_ROWS", 100_000, min: 0),
            ParallelWorkers        = EnvInt("LAPLACE_INGEST_WORKERS",     1,       min: 1),
            Progress               = progress,
        };
    }

    private static async Task<int> IngestViaRunnerAsync(
        IDecomposer dec, string ecosystemPath, bool skipLayerCheck)
    {
        CodepointPerfcache.Load(ResolveBlob());

        LanguageReference.EnsureLoaded();

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var loggerFactory = ConsoleLoggerProvider.Factory();
        var innerWriter = new NpgsqlSubstrateWriter(ds);
        await using var accumulator = new ConsensusAccumulatingWriter(innerWriter, ds,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        var writer = (ISubstrateWriter)accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory);

        Console.WriteLine($"ingest {dec.SourceName} via IngestRunner → {ConnString} ...");
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            dec,
            BuildIngestOptions(sw, dec.SourceName, skipLayerCheck, ecosystemPath),
            CancellationToken.None);
        sw.Stop();

        Console.WriteLine(
            $"done: {result.UnitsApplied:N0} intents applied, "
            + $"{result.EntitiesInserted:N0} novel entities, "
            + $"{result.PhysicalitiesInserted:N0} physicalities, "
            + $"{result.TotalRoundTrips:N0} round-trips, "
            + $"{sw.Elapsed.TotalSeconds:F1}s");
        if (result.Failures.Count > 0)
        {
            Console.Error.WriteLine($"failures: {result.Failures.Count}");
            return 1;
        }
        Console.WriteLine(
            $"consensus: folding {accumulator.ObservationsAccumulated:N0} matches "
            + $"across {accumulator.FoldWorkers} partition(s) ...");
        var materialized = await accumulator.MaterializeConsensusAsync();
        Console.WriteLine($"consensus: {materialized:N0} relations materialized "
                        + $"(accumulated at ingest; evidence = provenance-only)");
        await RegisterDynamicCanonicalsAsync(ds, dec.CanonicalNamesForReadback);
        try { await PrintCountsAsync(ds); }
        catch (Exception ex)
        { Console.Error.WriteLine($"warn: substrate counts diagnostic failed (ingest itself is complete): {ex.Message}"); }
        return 0;
    }

    private static async Task<int> StatsAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await PrintCountsAsync(ds);
        return 0;
    }

    private static async Task RegisterDynamicCanonicalsAsync(
        NpgsqlDataSource ds, IReadOnlyCollection<string> names)
    {
        if (names is null || names.Count == 0) return;
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT laplace.register_canonicals(@names)";
        cmd.Parameters.Add(new global::Npgsql.NpgsqlParameter
        {
            ParameterName = "names",
            Value = names as string[] ?? names.ToArray(),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
        });
        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"registered {names.Count:N0} canonical names");
    }

    private static async Task PrintCountsAsync(NpgsqlDataSource ds)
    {
        await using var conn = await ds.OpenConnectionAsync();

        Console.WriteLine("substrate counts:");
        {
            await using var counts = conn.CreateCommand();
            counts.CommandTimeout = 0;
            counts.CommandText = "SELECT metric, value FROM laplace.substrate_counts()";
            await using var rdr = await counts.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                Console.WriteLine($"  {rdr.GetString(0),-24}: {rdr.GetInt64(1),12:N0}");
        }

        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT laplace.render(laplace.canonical_id('A')), f.tier,
                                       p.x, p.y, p.z, p.m, encode(p.hilbert_index, 'hex')
                                FROM laplace.entity_facets(laplace.canonical_id('A')) f
                                CROSS JOIN laplace.entity_physicalities(laplace.canonical_id('A')) p
                                WHERE p.type = 1
                                  AND p.source_id = laplace.source_id('UnicodeDecomposer')";
            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                Console.WriteLine("  sample U+0041 'A':");
                Console.WriteLine($"    render    : {rdr.GetString(0)}  tier={rdr.GetInt16(1)}");
                Console.WriteLine($"    coord     : ({rdr.GetDouble(2):F6}, {rdr.GetDouble(3):F6}, {rdr.GetDouble(4):F6}, {rdr.GetDouble(5):F6})");
                Console.WriteLine($"    hilbert   : {rdr.GetString(6)}");
            }
            else
            {
                Console.WriteLine("  (no CONTENT physicality for U+0041 yet — run: laplace ingest unicode)");
            }
        }

        async Task<long> KindCount(string typeName)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = "SELECT laplace.evidence_count(p_type => laplace.relation_type_id($1))";
            c.Parameters.AddWithValue(typeName);
            return (long)(await c.ExecuteScalarAsync())!;
        }

        string[] modelKinds =
        [
            "EMBEDS", "Q_PROJECTS", "K_PROJECTS", "V_PROJECTS", "O_PROJECTS",
            "GATES", "UP_PROJECTS", "DOWN_PROJECTS", "NORM_SCALES", "OUTPUT_PROJECTS",
        ];
        long modelAtts = 0;
        var kindCounts = new long[modelKinds.Length];
        for (int i = 0; i < modelKinds.Length; i++)
            modelAtts += kindCounts[i] = await KindCount(modelKinds[i]);
        if (modelAtts == 0)
        {
            Console.WriteLine("  model attestations    : (none — ingest model)");
            return;
        }

        Console.WriteLine($"  model attestations    : {modelAtts,9:N0}");
        for (int i = 0; i < modelKinds.Length; i++)
            Console.WriteLine($"  └ {modelKinds[i],-16}: {kindCounts[i],9:N0}");
    }

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

    private static int Roundtrip(string path, string? outPath)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Fail($"usage: laplace roundtrip <file> [out]  (not found: {path})");
        CodepointPerfcache.Load(ResolveBlob());

        byte[] original = File.ReadAllBytes(path);

        var swIn = Stopwatch.StartNew();
        using var tree = TextDecomposer.Run(original);
        swIn.Stop();

        var swOut = Stopwatch.StartNew();
        int total = tree.NodeCount;
        int leaves = 0;
        var sb = new StringBuilder(original.Length);
        for (uint i = 0; i < total; i++)
        {
            var v = tree.GetNode(i);
            if (v.Tier != 0) break;
            sb.Append(char.ConvertFromUtf32((int)v.Atom));
            leaves++;
        }
        byte[] exported = Encoding.UTF8.GetBytes(sb.ToString());
        swOut.Stop();

        if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, exported);

        string hIn = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        string hOut = Convert.ToHexString(SHA256.HashData(exported)).ToLowerInvariant();
        bool match = hIn == hOut;

        double mbIn = original.Length / 1048576.0;
        Console.WriteLine($"ingest  : {original.Length,10:N0} bytes  →  {total:N0} tier-tree nodes ({leaves:N0} codepoints)  in {swIn.Elapsed.TotalMilliseconds:F0} ms  ({mbIn / swIn.Elapsed.TotalSeconds:F1} MB/s)");
        Console.WriteLine($"export  : {exported.Length,10:N0} bytes  in {swOut.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"sha256 in  : {hIn}");
        Console.WriteLine($"sha256 out : {hOut}");
        Console.WriteLine(match
            ? "BIT-PERFECT — export is byte-for-byte identical to the original."
            : "MISMATCH — export differs from the original.");
        return match ? 0 : 1;
    }

    private static readonly string[] TierName = { "CP", "GRAPHEME", "WORD", "SENTENCE", "DOC" };

    private static void PrintNode(TierTree tree, uint idx, int depth)
    {
        var v = tree.GetNode(idx);
        string label = v.Tier < TierName.Length ? TierName[v.Tier] : $"T{v.Tier}";
        string idHex = Hex(v.Id).Substring(0, 8);
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
