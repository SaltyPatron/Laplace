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
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
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
        var rows = await NpgsqlSubstrateReads.RecallSessionAsync(conn, prompt, session: null, default);
        if (echoPrompt) Console.WriteLine($"you      : {prompt}");
        bool any = false;
        foreach (var row in rows)
        {
            any = true;
            string mu = row.EffMu is null ? "" : $"  μ={row.EffMu.Value:F1}";
            string w = row.Witnesses is null ? "" : $" witnesses={row.Witnesses.Value}";
            Console.WriteLine($"substrate: {row.Reply}{mu}{w}");
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
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        var sw = Stopwatch.StartNew();

        Console.WriteLine($"goal     : {goal}");

        Console.WriteLine("── answer ─────────────────────────────────────────");
        bool any = false;
        foreach (var row in await NpgsqlSubstrateReads.RecallAsync(conn, goal, default))
        {
            any = true;
            string mu = row.EffMu is { } m ? $"  μ={m:F1}" : "";
            string w = row.Witnesses is { } wit ? $" witnesses={wit}" : "";
            Console.WriteLine($"  {row.Reply}{mu}{w}");
        }
        if (!any) Console.WriteLine("  (the substrate holds no answer to this yet)");

        Console.WriteLine("── gaps (unwitnessed arenas — the research agenda) ──");
        var gaps = (await NpgsqlSubstrateReads.GapsForPromptAsync(conn, goal, default))
            .Where(g => g.Length > 0).ToList();
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
        int k = 10;

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        var sw = Stopwatch.StartNew();

        byte[]? id = await NpgsqlSubstrateReads.FirstPlacedTopicAsync(conn, word, default);
        if (id is null)
        {
            Console.WriteLine($"  ('{word}' is not a placed content entity in this substrate)");
            return 1;
        }

        Console.WriteLine($"\n  '{word}' — STRUCTURAL (glome geodesic) + SHAPE (Fréchet)");
        Console.WriteLine($"  {"neighbor",-26} {"geodesic",10} {"frechet",10}");
        Console.WriteLine($"  {new string('-', 26)} {new string('-', 10)} {new string('-', 10)}");
        bool anyStructural = false;
        foreach (var nbRow in await NpgsqlSubstrateReads.NearestNeighbors4dAsync(conn, word, k, default))
        {
            anyStructural = true;
            string nb = nbRow.Neighbor;
            if (nb.Length > 26) nb = nb[..25] + "…";
            string g = nbRow.Geodesic.ToString("F6");
            string f = nbRow.Frechet is { } fr ? fr.ToString("F4") : "—";
            Console.WriteLine($"  {nb,-26} {g,10} {f,10}");
        }
        if (!anyStructural)
            Console.WriteLine($"  (‘{word}’ is not a placed content entity in this substrate)");

        if (id is not null)
        {
            Console.WriteLine($"\n  '{word}' — SEMANTIC (consensus μ via salient_facts)");
            Console.WriteLine($"  {"type",-22} {"fact",-28} {"eff_mu",10} {"wit",4}");
            Console.WriteLine($"  {new string('-', 22)} {new string('-', 28)} {new string('-', 10)} {new string('-', 4)}");
            foreach (var factRow in await NpgsqlSubstrateReads.SalientFactsAsync(conn, id, k, default))
            {
                string fact = factRow.Fact;
                if (fact.Length > 28) fact = fact[..27] + "…";
                Console.WriteLine($"  {factRow.Type,-22} {fact,-28} {(long)Math.Round(factRow.EffMu),10:N0} {factRow.Witnesses,4}");
            }
        }

        sw.Stop();
        Console.WriteLine($"\n  [{sw.Elapsed.TotalMilliseconds:F1} ms — two co-equal axes, read-only, no GPU]\n");
        return 0;
    }

    public static async Task<int> WalkAsync(string[] args)
    {
        const int steps = 20;
        const int order = 5;
        const int topk = 8;
        const double temp = 0.6;
        const bool verbose = false;

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);

        string prompt = string.Join(' ', args).Trim();
        if (!string.IsNullOrWhiteSpace(prompt))
            return await WalkOnceAsync(ds, prompt, steps, order, temp, topk, verbose);

        Console.WriteLine("laplace walk — type a prompt, Enter. Blank line or Ctrl-D quits.");
        while (true)
        {
            Console.Write("\nprompt> ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            await WalkOnceAsync(ds, line, steps, order, temp, topk, verbose);
        }
        return 0;
    }

    private static async Task<int> WalkOnceAsync(
        NpgsqlDataSource ds, string prompt, int steps, int order, double temp, int topk, bool verbose)
    {
        var sw = Stopwatch.StartNew();
        var toks = new List<(string entity, int strideUsed)>();
        await foreach (var row in NpgsqlSubstrateReads.WalkTextAsync(
            ds, prompt, steps, order, temp, topk, default))
            toks.Add((row.Entity, row.StrideUsed));
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

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();

        // chat() IS THE ENTRY POINT. This command used to call laplace.walk_text()
        // directly with four hardcoded knobs (steps 48, order 5, temp 0.6, topk 8),
        // which made the CLI a SIBLING entry point to the forward pass rather than a
        // caller of it — the one thing CLAUDE.md and spec 36 forbid outright:
        // "chat() is the only conversational entry point and runs the full ladder;
        // converse, converse_about, converse_walk, converse_facts are internal
        // STAGES of it, never sibling entry points."
        //
        // Going straight to walk_text skipped every stage that makes a turn a turn:
        // language inference from the prompt, the native specificity election
        // (prompt_coherence), shape dispatch, the band lens, the responder family,
        // and converse_about. A CLI answer and an API answer to the same prompt were
        // produced by different machinery and could not be compared.
        var response = await NpgsqlSubstrateReads.ChatAsync(
            conn, prompt, SessionId.ToBytes(), default) ?? string.Empty;
        Console.WriteLine(response);

        // CLOSE through the shared lane (Laplace.Ingestion.TurnCloser), the same one
        // the MCP tool and the HTTP endpoint use. This previously deposited through
        // the plain untenanted UserPrompt/Response sources, so a CLI turn carried no
        // session, no tenant and no attribution — spec 34's conversational
        // provenance did not apply to it at all, and its turns could not be
        // distinguished from an agent's standalone note.
        await using var closer = new TurnCloser(ds, w => Console.Error.WriteLine($"laplace: {w}"));
        if (await closer.CloseAsync(CliTenant, SessionId, prompt, response))
            Console.WriteLine($"    [turn deposited @ session {Convert.ToHexStringLower(SessionId.ToBytes())[..16]} "
                + $"— content-addressed, citable, self-extending]");

        return 0;
    }

    /// <summary>
    /// The CLI's conversational identity. One tenant for the lane, one session per
    /// process, minted through the same canonical id law the MCP tool and the HTTP
    /// surface use — so a CLI session and an endpoint session with the same
    /// tenant+key are the SAME context entity, not two.
    /// </summary>
    private const string CliTenant = "cli-local";
    private static readonly Hash128 SessionId =
        ConversationContent.SessionId(CliTenant, $"s-{Guid.NewGuid():N}");




    public static async Task<int> AttestAsync(string[] args)
    {
        const string usage = "usage: laplace attest <confirm|refute> <tok1> [tok2...]\n"
            + "       laplace attest <confirm|refute> <subject> <RELATION_TYPE> <object>";
        if (args.Length < 2)
            return Fail(usage);

        string mode = args[0].ToLowerInvariant();
        bool confirm = mode == "confirm";
        if (!confirm && mode != "refute")
            return Fail(usage);

        string[] tokens = args[1..];

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);

        CodepointPerfcache.LoadDefault();

        // Triple mode: exactly <subject> <RELATION_TYPE> <object> where the middle
        // token is a canonical relation name (uppercase, e.g. IS_A). Anything else
        // is the PRECEDES-chain form. Both go through the ONE feedback lane
        // (FeedbackContent, doc 15 G1/G2).
        if (tokens.Length == 3 && FeedbackContent.TryResolveRelation(tokens[1], out var rel))
            return await AttestTripleAsync(ds, tokens[0], rel, tokens[2], confirm);

        var resolved = await FeedbackContent.ResolveTokensAsync(ds, tokens);
        var ids = new List<Hash128>(tokens.Length);
        foreach (var t in resolved)
        {
            if (t.Id is null)
            {
                Console.WriteLine($"  warn: '{t.Token}' is empty — skipping");
                continue;
            }
            if (!t.Present)
            {
                Console.WriteLine($"  warn: '{t.Token}' has no substrate entity — skipping");
                continue;
            }
            ids.Add(t.Id.Value);
        }

        if (ids.Count < 2)
        {
            Console.WriteLine($"  attest: need ≥2 resolved tokens for a PRECEDES pair (got {ids.Count})");
            return ids.Count == 0 ? 1 : 0;
        }

        var result = await FeedbackContent.ApplyAsync(ds, FeedbackContent.BuildPrecedesChain(ids, confirm));
        Console.WriteLine($"  applied: {result.AttestationsInserted} attestation(s) inserted");
        Console.WriteLine($"  consensus: {result.ConsensusUpdated} relation(s) updated "
            + $"({(confirm ? "↑ confirmed" : "↓ refuted")} {ids.Count - 1} PRECEDES pair(s))");
        return 0;
    }

    private static async Task<int> AttestTripleAsync(
        NpgsqlDataSource ds, string subject, RelationTypeRegistry.RelationTypeResolution rel,
        string obj, bool confirm)
    {
        var resolved = await FeedbackContent.ResolveTokensAsync(ds, [subject, obj]);
        foreach (var t in resolved)
        {
            if (t.Usable) continue;
            Console.WriteLine(t.Id is null
                ? $"  warn: '{t.Token}' is empty"
                : $"  warn: '{t.Token}' has no substrate entity");
            return 1;
        }

        Hash128 subjectId = resolved[0].Id!.Value;
        Hash128 objectId = resolved[1].Id!.Value;

        var before = await FeedbackContent.ConsensusStateAsync(ds, subjectId, rel.Id, objectId);
        Console.WriteLine(before is null
            ? $"  target: {subject} {rel.Canonical} {obj} — NEW claim (no consensus row yet)"
            : $"  target: {subject} {rel.Canonical} {obj} — existing row "
                + $"(rating {before.Rating}, rd {before.Rd}, witnesses {before.WitnessCount})");

        var result = await FeedbackContent.ApplyAsync(
            ds, FeedbackContent.BuildTriple(subjectId, rel.Canonical, objectId, confirm));
        Console.WriteLine($"  applied: {result.AttestationsInserted} attestation(s) inserted");

        var after = await FeedbackContent.ConsensusStateAsync(ds, subjectId, rel.Id, objectId);
        Console.WriteLine($"  consensus: {result.ConsensusUpdated} relation(s) updated "
            + $"({(confirm ? "↑ confirmed" : "↓ refuted")} 1 {rel.Canonical} triple)");
        if (after is not null)
            Console.WriteLine($"  now: rating {after.Rating}, rd {after.Rd}, witnesses {after.WitnessCount}"
                + (before is null ? "" : $" (Δrating {after.Rating - before.Rating:+#;-#;0}, Δwitnesses {after.WitnessCount - before.WitnessCount:+#;-#;0})"));
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

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();

        bool exists = false;
        {
            var facet = await NpgsqlSubstrateReads.EntityFacetsAsync(conn, id.ToBytes(), default);
            if (facet is { } f)
            {
                exists = true;
                Console.WriteLine($"  ENTITY: present  tier={f.Tier}  type={f.Type}");
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

            // ONE round-trip via shared catalog reader; bucket by input ordinal in C#.
            var buckets = new Dictionary<int, List<(string Type, string? Obj, decimal Mu, long Wit)>>();
            if (words.Count > 0)
            {
                var idsArr = new byte[words.Count][];
                for (int i = 0; i < words.Count; i++) idsArr[i] = words[i].Id.ToBytes();
                foreach (var row in await NpgsqlSubstrateReads.ConsensusOutReadableBatchAsync(
                             conn, idsArr, 2, default))
                {
                    int ord = (int)row.Ordinal;
                    if (!buckets.TryGetValue(ord, out var list))
                    {
                        list = new List<(string, string?, decimal, long)>();
                        buckets[ord] = list;
                    }
                    list.Add((row.Type, row.Object, row.EffMu, row.Witnesses));
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

        {
            var placements = await NpgsqlSubstrateReads.EntityPhysicalitiesAsync(conn, id.ToBytes(), default);
            Console.WriteLine("\n  GLOME (physicalities):");
            if (placements.Count == 0)
                Console.WriteLine("    (none)");
            else
            {
                foreach (var p in placements)
                {
                    Console.WriteLine($"    type={p.Type}  coord=({p.X:F4},{p.Y:F4},{p.Z:F4},{p.M:F4})"
                        + $"  r={p.Radius:F6}  n_constituents={p.Constituents}");
                }
            }
        }

        {
            Console.WriteLine("\n  OUTGOING consensus (this → object), Glicko-2 μ over all witnesses:");
            int n = 0;
            foreach (var c in await NpgsqlSubstrateReads.ConsensusOutRenderedAsync(conn, id.ToBytes(), default))
            {
                n++;
                string obj = c.PeerLabel ?? "(unary)";
                Console.WriteLine($"    [{c.TypeLabel}] → {obj,-28}  μ={c.Rating / 1e9:F3} rd={c.Rd / 1e9:F3} σ={c.Volatility / 1e9:F4}"
                    + $"  witnesses={c.WitnessCount}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        {
            Console.WriteLine("\n  INCOMING consensus (subject → this), Glicko-2 μ over all witnesses:");
            int n = 0;
            foreach (var c in await NpgsqlSubstrateReads.ConsensusInRenderedAsync(conn, id.ToBytes(), default))
            {
                n++;
                string subj = c.PeerLabel ?? "?";
                Console.WriteLine($"    {subj,-28} [{c.TypeLabel}] → here  μ={c.Rating / 1e9:F3} rd={c.Rd / 1e9:F3} σ={c.Volatility / 1e9:F4}"
                    + $"  witnesses={c.WitnessCount}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        static string Outc(short o) => o switch { 0 => "refute", 1 => "draw", _ => "confirm" };
        {
            Console.WriteLine("\n  OUTGOING evidence (provenance — who witnessed):");
            int n = 0;
            foreach (var a in await NpgsqlSubstrateReads.AttestationsOutRenderedAsync(conn, id.ToBytes(), default))
            {
                n++;
                string obj = a.PeerLabel ?? "(unary)";
                string ctx = a.ContextId is null ? "-" : Hex(ReadHash16(a.ContextId))[..10] + "…";
                Console.WriteLine($"    [{a.TypeLabel}] → {obj,-28}  {Outc(a.Outcome)}"
                    + $"  src={a.SourceLabel}  ctx={ctx}  games={a.ObservationCount}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        {
            Console.WriteLine("\n  INCOMING evidence (provenance — who witnessed):");
            int n = 0;
            foreach (var a in await NpgsqlSubstrateReads.AttestationsInRenderedAsync(conn, id.ToBytes(), default))
            {
                n++;
                string subj = a.PeerLabel ?? "?";
                string ctx = a.ContextId is null ? "-" : Hex(ReadHash16(a.ContextId))[..10] + "…";
                Console.WriteLine($"    {subj,-28} [{a.TypeLabel}] → here  {Outc(a.Outcome)}"
                    + $"  src={a.SourceLabel}  ctx={ctx}  games={a.ObservationCount}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        return 0;
    }
}
