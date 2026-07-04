using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.Code;
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
using Laplace.Decomposers.Image;
using Laplace.Decomposers.Audio;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Dynamics;
using DynamicsInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static class QueryCommands
{
    private static Hash128 ReadHash16(byte[] b) =>
        new Hash128(BitConverter.ToUInt64(b, 0), BitConverter.ToUInt64(b, 8));

    public static async Task<int> ConverseAsync(string prompt)
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
        cmd.CommandText = "SELECT reply, eff_mu, witnesses FROM laplace.recall_session(@p)";
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

    public static async Task<int> RecallAsync(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            Console.Error.WriteLine("usage: laplace recall \"<goal>\"  (e.g. \"what is a dog\", \"how are whale and dolphin related\")");
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
            cmd.CommandTimeout = 120;
            cmd.CommandText = "SELECT reply, eff_mu, witnesses FROM laplace.recall(@p)";
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
        Console.WriteLine($"           [{sw.Elapsed.TotalMilliseconds:F1} ms, intent-routed consensus reads]");
        return 0;
    }

    public static async Task<int> NeighborsAsync(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return Fail("usage: laplace neighbors <word>");
        int k = EnvInt("LAPLACE_NN_K", 10, 1);

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();
        var sw = Stopwatch.StartNew();

        byte[]? id = null;
        await using (var res = conn.CreateCommand())
        {
            res.CommandText = "SELECT laplace.first_placed_topic(@w)";
            res.Parameters.AddWithValue("w", word);
            var scalar = await res.ExecuteScalarAsync();
            if (scalar is byte[] b) id = b;
        }
        if (id is null)
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
            st.CommandText =
                "SELECT neighbor, geodesic, frechet FROM laplace.nearest_neighbors_4d(@w, @k)";
            st.Parameters.AddWithValue("w", word);
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
            Console.WriteLine($"\n  '{word}' — SEMANTIC (consensus μ via salient_facts)");
            Console.WriteLine($"  {"type",-22} {"fact",-28} {"eff_mu",10} {"wit",4}");
            Console.WriteLine($"  {new string('-', 22)} {new string('-', 28)} {new string('-', 10)} {new string('-', 4)}");
            await using var se = conn.CreateCommand();
            se.CommandText =
                "SELECT type, fact, round(eff_mu,0)::bigint, witnesses "
                + "FROM laplace.salient_facts(@id) ORDER BY eff_mu DESC LIMIT @k";
            se.Parameters.AddWithValue("id", id);
            se.Parameters.AddWithValue("k", k);
            await using var r = await se.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string relType = r.IsDBNull(0) ? "" : r.GetString(0);
                string fact = r.IsDBNull(1) ? "" : r.GetString(1);
                if (fact.Length > 28) fact = fact[..27] + "…";
                string mu = r.IsDBNull(2) ? "" : r.GetInt64(2).ToString("N0");
                string wit = r.IsDBNull(3) ? "" : r.GetInt64(3).ToString();
                Console.WriteLine($"  {relType,-22} {fact,-28} {mu,10} {wit,4}");
            }
        }

        sw.Stop();
        Console.WriteLine($"\n  [{sw.Elapsed.TotalMilliseconds:F1} ms — two co-equal axes, read-only, no GPU]\n");
        return 0;
    }

    public static async Task<int> WalkAsync(string[] args)
    {
        int steps = EnvInt("LAPLACE_GEN_STEPS", 20, 1);
        int order = EnvInt("LAPLACE_GEN_ORDER", EnvInt("LAPLACE_GEN_WINDOW", 5, 1), 1);
        int topk = EnvInt("LAPLACE_GEN_TOPK", 8, 1);
        double temp = EnvDouble("LAPLACE_GEN_TEMP", 0.6);
        bool verbose = Environment.GetEnvironmentVariable("LAPLACE_GEN_VERBOSE") == "1";

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();

        string prompt = string.Join(' ', args).Trim();
        if (!string.IsNullOrWhiteSpace(prompt))
            return await WalkOnceAsync(conn, prompt, steps, order, temp, topk, verbose);

        Console.WriteLine("laplace walk — type a prompt, Enter. Blank line or Ctrl-D quits.");
        while (true)
        {
            Console.Write("\nprompt> ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            await WalkOnceAsync(conn, line, steps, order, temp, topk, verbose);
        }
        return 0;
    }

    private static async Task<int> WalkOnceAsync(
        NpgsqlConnection conn, string prompt, int steps, int order, double temp, int topk, bool verbose)
    {
        var sw = Stopwatch.StartNew();
        var toks = new List<(string entity, int strideUsed)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT step, entity, stride_used FROM laplace.walk_text("
                + "@prompt, @steps, @order, @temp, @topk)";
            cmd.Parameters.AddWithValue("prompt", prompt);
            cmd.Parameters.AddWithValue("steps", steps);
            cmd.Parameters.AddWithValue("order", order);
            cmd.Parameters.AddWithValue("temp", temp);
            cmd.Parameters.AddWithValue("topk", topk);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                toks.Add((r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? 0 : r.GetInt32(2)));
        }
        sw.Stop();


        Console.WriteLine(prompt + string.Concat(toks.Select(t => t.entity)));
        if (verbose)
            for (int i = 0; i < toks.Count; i++)
                Console.WriteLine($"    {i + 1,2}. {toks[i].entity,-22} stride={toks[i].strideUsed}");
        Console.WriteLine($"    [{toks.Count} entities, {sw.Elapsed.TotalMilliseconds:F0} ms — native stride descent, no GPU]");
        return 0;
    }





    public static async Task<int> ChatAsync(string[] args)
    {
        string prompt = string.Join(' ', args).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return Fail("usage: laplace chat <prompt>");

        CodepointPerfcache.Load(ResolveBlob());

        int steps = EnvInt("LAPLACE_GEN_STEPS", 48, 1);
        int order = EnvInt("LAPLACE_GEN_ORDER", EnvInt("LAPLACE_GEN_WINDOW", 5, 1), 1);
        int topk = EnvInt("LAPLACE_GEN_TOPK", 8, 1);
        double temp = EnvDouble("LAPLACE_GEN_TEMP", 0.6);

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();

        var inner = new NpgsqlSubstrateWriter(ds);
        await using var acc = new ConsensusAccumulatingWriter(inner, ds);
        var writer = (ISubstrateWriter)acc;


        await writer.ApplyAsync(ResponseContent.BuildBootstrapChange(), CancellationToken.None);


        Hash128 promptRoot = Hash128.Zero;
        if (UserPromptContent.TryBuildWitnessChange(Encoding.UTF8.GetBytes(prompt), "chat/prompt", out var pc, out var pr))
        { await writer.ApplyAsync(pc, CancellationToken.None); promptRoot = pr; }


        var sb = new StringBuilder();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT entity FROM laplace.walk_text(@p, @steps, @order, @temp, @topk)";
            cmd.Parameters.AddWithValue("p", prompt);
            cmd.Parameters.AddWithValue("steps", steps);
            cmd.Parameters.AddWithValue("order", order);
            cmd.Parameters.AddWithValue("temp", temp);
            cmd.Parameters.AddWithValue("topk", topk);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) if (!r.IsDBNull(0)) sb.Append(r.GetString(0));
        }
        string response = sb.ToString();
        Console.WriteLine(prompt + response);


        if (response.Length > 0 &&
            ResponseContent.TryBuildWitnessChange(Encoding.UTF8.GetBytes(response), "chat/response",
                promptRoot == Hash128.Zero ? null : promptRoot, out var rc, out var rr))
        {
            await writer.ApplyAsync(rc, CancellationToken.None);
            Console.WriteLine($"    [Response deposited: {Convert.ToHexString(rr.ToBytes())[..16].ToLowerInvariant()} "
                + $"@ trust {SourceTrust.Response} — content-addressed, citable, self-extending]");
        }

        await acc.MaterializeConsensusAsync();
        return 0;
    }




    public static async Task<int> AttestAsync(string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: laplace attest <confirm|refute> <tok1> [tok2...]");

        string mode = args[0].ToLowerInvariant();
        bool confirm = mode == "confirm";
        if (!confirm && mode != "refute")
            return Fail("usage: laplace attest <confirm|refute> <tok1> [tok2...]");

        string[] tokens = args[1..];

        var feedbackSource = Hash128.OfCanonical("substrate/source/UserFeedback/v1");

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();

        CodepointPerfcache.LoadDefault();

        // Resolve every token first, then test presence in ONE batched round-trip via the
        // existing entities_exist_bitmap primitive. Pass 2 replays token order so the warn/skip
        // messages appear in exactly the same sequence as the old per-token EXISTS loop.
        var resolved = new Hash128?[tokens.Length];
        var probeIds = new List<Hash128>(tokens.Length);
        for (int i = 0; i < tokens.Length; i++)
        {
            var rid = TextDecomposer.ContentRootId(tokens[i]);
            resolved[i] = rid;
            if (rid is not null) probeIds.Add(rid.Value);
        }

        var reader = new NpgsqlSubstrateReader(ds);
        var bitmap = await reader.EntitiesExistBitmapAsync(probeIds);

        var ids = new List<(string Token, Hash128 Id)>(tokens.Length);
        int probeBit = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            var rid = resolved[i];
            if (rid is null)
            {
                Console.WriteLine($"  warn: '{tokens[i]}' is empty — skipping");
                continue;
            }
            bool present = (bitmap[probeBit >> 3] & (1 << (probeBit & 7))) != 0;
            probeBit++;
            if (!present)
            {
                Console.WriteLine($"  warn: '{tokens[i]}' has no substrate entity — skipping");
                continue;
            }
            ids.Add((tokens[i], rid.Value));
        }

        if (ids.Count < 2)
        {
            Console.WriteLine($"  attest: need ≥2 resolved tokens for a PRECEDES pair (got {ids.Count})");
            return ids.Count == 0 ? 1 : 0;
        }

        var b = new SubstrateChangeBuilder(feedbackSource, "attest/0", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: ids.Count - 1);

        for (int i = 0; i + 1 < ids.Count; i++)
            b.AddAttestation(NativeAttestation.Categorical(
                ids[i].Id, "PRECEDES", ids[i + 1].Id,
                feedbackSource, null, SourceTrust.UserPrompt, confirm: confirm));

        var change = b.Build();
        var inner = new NpgsqlSubstrateWriter(ds);
        await using var acc = new ConsensusAccumulatingWriter(inner, ds);
        var result = await ((ISubstrateWriter)acc).ApplyAsync(change, CancellationToken.None);
        Console.WriteLine($"  applied: {result.AttestationsInserted} attestation(s) inserted");

        var materialized = await acc.MaterializeConsensusAsync();
        Console.WriteLine($"  consensus: {materialized} relation(s) updated "
            + $"({(confirm ? "↑ confirmed" : "↓ refuted")} {ids.Count - 1} PRECEDES pair(s))");
        return 0;
    }

    public static async Task<int> InspectAsync(string text)
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
        var wordSeen = new HashSet<Hash128>();
        var words = new List<(Hash128 Id, string Label)>();
        for (uint i = 0; i < (uint)tree.NodeCount; i++)
        {
            var v = tree.GetNode(i);
            if (v.Tier != EntityTier.Word) continue;
            if (!wordSeen.Add(v.Id)) continue;
            words.Add((v.Id, Encoding.UTF8.GetString(utf8Input, (int)v.TextRangeOff, (int)v.TextRangeLen)));
        }

        if (!exists)
            Console.WriteLine("  ENTITY: novel composite — correct id, not yet ingested (a prompt ingest binds it)");

        if (words.Count > 1 || !exists)
        {
            Console.WriteLine("\n  CONSTITUENT KNOWLEDGE (the substrate answering through the parts it knows):");

            // ONE round-trip: fan the word ids through consensus_out_readable via LATERAL, then
            // bucket by input ordinal in C#. ORDER BY u.ord, r.rord keeps each word's rows in the
            // function's own ranking order, identical to the old per-word loop.
            var buckets = new Dictionary<int, List<(string Type, string? Obj, decimal Mu, long Wit)>>();
            if (words.Count > 0)
            {
                await using var wc = conn.CreateCommand();
                wc.CommandText =
                    "SELECT u.ord, r.type, r.object, r.eff_mu, r.witnesses "
                    + "FROM unnest(@ids::bytea[]) WITH ORDINALITY AS u(id, ord) "
                    + "CROSS JOIN LATERAL laplace.consensus_out_readable(u.id, 2) "
                    + "WITH ORDINALITY AS r(type, object, eff_mu, witnesses, rord) "
                    + "ORDER BY u.ord, r.rord";
                var idsArr = new byte[words.Count][];
                for (int i = 0; i < words.Count; i++) idsArr[i] = words[i].Id.ToBytes();
                var p = wc.Parameters.AddWithValue("ids", idsArr);
                p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                await using var wr = await wc.ExecuteReaderAsync();
                while (await wr.ReadAsync())
                {
                    int ord = (int)wr.GetInt64(0);
                    if (!buckets.TryGetValue(ord, out var list))
                    {
                        list = new List<(string, string?, decimal, long)>();
                        buckets[ord] = list;
                    }
                    list.Add((
                        wr.GetString(1),
                        wr.IsDBNull(2) ? null : wr.GetString(2),
                        wr.GetDecimal(3),
                        wr.GetInt64(4)));
                }
            }

            for (int i = 0; i < words.Count; i++)
            {
                var (_, label) = words[i];
                if (buckets.TryGetValue(i + 1, out var list) && list.Count > 0)
                {
                    Console.WriteLine($"    \"{label}\"");
                    foreach (var (type, obj, mu, wit) in list)
                        Console.WriteLine($"        [{type}] → {obj ?? "(unary)"}  μ={mu:F3}  witnesses={wit}");
                }
                else
                {
                    Console.WriteLine($"    \"{label}\"  (no consensus yet)");
                }
            }
        }

        if (!exists) return 0;

        await using (var cmd = conn.CreateCommand())
        {

            cmd.CommandText = "SELECT p.type, p.x, p.y, p.z, p.m, p.radius, p.n_constituents "
                            + "FROM laplace.entity_physicalities(@id) p";
            cmd.Parameters.AddWithValue("id", id.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync();
            Console.WriteLine("\n  GLOME (physicalities):");
            int n = 0;
            while (await r.ReadAsync())
            {
                n++;
                Console.WriteLine($"    type={r.GetInt16(0)}  coord=({r.GetDouble(1):F4},{r.GetDouble(2):F4},{r.GetDouble(3):F4},{r.GetDouble(4):F4})"
                    + $"  r={r.GetDouble(5):F6}  n_constituents={r.GetInt32(6)}");
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
                string relType = r.GetString(0);
                string obj = r.IsDBNull(1) ? "(unary)" : r.GetString(1);
                Console.WriteLine($"    [{relType}] → {obj,-28}  μ={r.GetInt64(2) / 1e9:F3} rd={r.GetInt64(3) / 1e9:F3} σ={r.GetInt64(4) / 1e9:F4}"
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
                Console.WriteLine($"    {r.GetString(0),-28} [{r.GetString(1)}] → here  μ={r.GetInt64(2) / 1e9:F3} rd={r.GetInt64(3) / 1e9:F3} σ={r.GetInt64(4) / 1e9:F4}"
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
}
