using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Data;
using Laplace.Engine.Core;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static class QueryCommands
{
    public static async Task<int> ConverseAsync(string prompt)
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();

        if (!string.IsNullOrWhiteSpace(prompt))
            return await ConverseTurnAsync(conn, prompt);

        byte[] session = RandomNumberGenerator.GetBytes(16);
        Console.WriteLine("laplace converse — one turn per line, empty line or Ctrl+D to leave.");
        while (true)
        {
            Console.Write("you      : ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            await ConverseTurnAsync(conn, line, echoPrompt: false, session: session);
        }
        return 0;
    }

    private static async Task<int> ConverseTurnAsync(NpgsqlConnection conn, string prompt, bool echoPrompt = true, byte[]? session = null)
    {
        var sw = Stopwatch.StartNew();
        var rows = await NpgsqlSubstrateReads.RecallSessionAsync(conn, prompt, session, default);
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
            Console.Error.WriteLine("usage: laplace recall \"<goal>\"");
            return 2;
        }
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        var sw = Stopwatch.StartNew();

        Console.WriteLine($"goal     : {goal}");
        Console.WriteLine("── answer ─────────────────────────────────────────");
        bool any = false;
        foreach (var row in await NpgsqlSubstrateReads.RecallSessionAsync(conn, goal, null, default))
        {
            any = true;
            string mu = row.EffMu is { } m ? $"  μ={m:F1}" : "";
            string w = row.Witnesses is { } wit ? $" witnesses={wit}" : "";
            Console.WriteLine($"  {row.Reply}{mu}{w}");
        }
        if (!any) Console.WriteLine("  (the substrate holds no answer to this yet)");

        sw.Stop();
        Console.WriteLine($"           [{sw.Elapsed.TotalMilliseconds:F1} ms, consensus read]");
        return 0;
    }

    public static async Task<int> NeighborsAsync(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return Fail("usage: laplace neighbors <word>");
        int k = 10;

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        var sw = Stopwatch.StartNew();

        byte[]? id = await NpgsqlSubstrateReads.ResolveRefAsync(ds, word, default);
        if (id is null)
        {
            Console.WriteLine($"  ('{word}' is not a placed content entity in this substrate)");
            return 1;
        }

        Console.WriteLine($"\n  '{word}' — STRUCTURAL (glome geodesic) + SHAPE (Fréchet)");
        Console.WriteLine($"  {"neighbor",-26} {"geodesic",10} {"frechet",10}");
        Console.WriteLine($"  {new string('-', 26)} {new string('-', 10)} {new string('-', 10)}");
        bool anyStructural = false;
        foreach (var nbRow in await NpgsqlSubstrateReads.StructuralNeighborsAsync(conn, id, k, default))
        {
            anyStructural = true;
            string nb = nbRow.Label ?? nbRow.IdHex ?? "?";
            if (nb.Length > 26) nb = nb[..25] + "…";
            string g = nbRow.Geodesic.ToString("F6");
            string f = nbRow.Frechet is { } fr ? fr.ToString("F4") : "—";
            Console.WriteLine($"  {nb,-26} {g,10} {f,10}");
        }
        if (!anyStructural)
            Console.WriteLine($"  (‘{word}’ is not a placed content entity in this substrate)");

        Console.WriteLine($"\n  '{word}' — SEMANTIC (consensus μ via salient_facts)");
        Console.WriteLine($"  {"type",-22} {"fact",-28} {"eff_mu",10} {"wit",4}");
        Console.WriteLine($"  {new string('-', 22)} {new string('-', 28)} {new string('-', 10)} {new string('-', 4)}");
        foreach (var factRow in await NpgsqlSubstrateReads.SalientFactsAsync(conn, id, k, default))
        {
            string fact = factRow.Fact;
            if (fact.Length > 28) fact = fact[..27] + "…";
            Console.WriteLine($"  {factRow.Type,-22} {fact,-28} {(long)Math.Round(factRow.EffMu),10:N0} {factRow.Witnesses,4}");
        }

        sw.Stop();
        Console.WriteLine($"\n  [{sw.Elapsed.TotalMilliseconds:F1} ms — two co-equal axes, read-only, no GPU]\n");
        return 0;
    }

    /// <summary>
    /// The forward program read, nothing witnessed (<c>lp_engine_turn</c> read-only).
    /// </summary>
    public static async Task<int> WalkAsync(string[] args)
    {
        using var runtime = new LaplaceEngineRuntime();
        string prompt = string.Join(' ', args).Trim();
        if (!string.IsNullOrWhiteSpace(prompt))
            return await WalkOnceAsync(runtime, prompt);

        Console.WriteLine("laplace walk — type a prompt, Enter. Blank line or Ctrl-D quits.");
        while (true)
        {
            Console.Write("\nprompt> ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            await WalkOnceAsync(runtime, line);
        }
        return 0;
    }

    private static Task<EngineTurnResponse> TurnAsync(
        LaplaceEngineRuntime runtime,
        string participant,
        string session,
        string prompt,
        bool readOnly,
        CancellationToken ct = default) =>
        Task.Run(() => runtime.Turn(new EngineTurnRequest(
            Prompt: prompt,
            User: participant,
            Session: session,
            ReadOnly: readOnly)), ct);

    private static async Task<int> WalkOnceAsync(LaplaceEngineRuntime runtime, string prompt)
    {
        var sw = Stopwatch.StartNew();
        var turn = await TurnAsync(runtime, CliParticipant, CliSession, prompt, readOnly: true);
        sw.Stop();
        Console.WriteLine(turn.Text);
        Console.WriteLine($"    [{turn.Work.tokens_out} tokens, {turn.Work.db_probes} probes, {sw.Elapsed.TotalMilliseconds:F0} ms — {turn.Disposition}; a read, nothing witnessed]");
        return turn.StatusCode == 0 ? 0 : 1;
    }

    /// <summary>
    /// One turn of the CLI's session, answered by the forward program and witnessed in-process via <c>lp_engine_turn</c>.
    /// </summary>
    public static async Task<int> ChatAsync(string[] args)
    {
        string prompt = string.Join(' ', args).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return Fail("usage: laplace chat <prompt>");

        using var runtime = new LaplaceEngineRuntime();
        var turn = await TurnAsync(runtime, CliParticipant, CliSession, prompt, readOnly: false);
        Console.WriteLine(turn.Text);
        Console.WriteLine(turn.StatusCode == 0
            ? $"    [turn {turn.TurnId.ToHex()} in session \"{CliSession}\" of {CliParticipant}; {turn.Disposition}]"
            : $"    [not witnessed ({turn.Error}); {turn.Disposition}]");
        return turn.StatusCode == 0 ? 0 : 1;
    }

    private static string CliParticipant => Environment.UserName;

    private static string CliSession =>
        Environment.GetEnvironmentVariable("LAPLACE_SESSION_KEY") is { Length: > 0 } k && IsValidIdentifier(k)
            ? k
            : "cli";

    private static bool IsValidIdentifier(string value)
    {
        if (value.Length is < 1 or > 128) return false;
        foreach (char ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '@' or '-'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The operator confirms or refutes one claim via <c>laplace witness</c> (<c>.statement</c>).
    /// </summary>
    public static async Task<int> AttestAsync(string[] args)
    {
        const string usage = "usage: laplace attest <confirm|refute> <subject> <relation> [object]";
        if (args.Length is < 3 or > 4)
            return Fail(usage);
        string mode = args[0].ToLowerInvariant();
        bool confirm = mode == "confirm";
        if (!confirm && mode != "refute")
            return Fail(usage);
        string subject = args[1], relation = args[2], obj = args.Length == 4 ? args[3] : "";

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        var before = await NpgsqlClaimStanding.ReadAsync(ds, subject, relation, obj);

        static string Esc(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);

        var workDir = Path.Combine(Path.GetTempPath(), "laplace-cli-attest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            string stmtPath = Path.Combine(workDir, "feedback.statement");
            string score = confirm ? "1.0" : "0.0";
            string line = string.Create(CultureInfo.InvariantCulture, $"{Esc(subject)}\t{Esc(relation)}\t{Esc(obj)}\t{score}\n");
            await File.WriteAllTextAsync(stmtPath, line, new UTF8Encoding(false));
            int rc = await NativeRecipeCatalog.RunNativeAsync(
                ["witness", "--as", Environment.UserName, "--class", "UserPromptContent", "--lineage", Environment.UserName, "--root", workDir, stmtPath],
                NativeRecipeCatalog.ConnectionInfoFromNpgsql(ConnString));
            if (rc != 0)
                return rc;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }

        var after = await NpgsqlClaimStanding.ReadAsync(ds, subject, relation, obj);
        string claim = obj.Length == 0 ? $"[{subject}, {relation}]" : $"[{subject}, {relation}, {obj}]";
        Console.WriteLine($"  {(confirm ? "confirmed" : "refuted")} {claim} as {Environment.UserName} (witness {Hash128.Text(Environment.UserName).ToHex()})");
        Console.WriteLine(before is null
            ? "  before: no standing"
            : $"  before: rating {before.Value.Rating:F1}, deviation {before.Value.Deviation:F1}, {before.Value.Matches} games");
        Console.WriteLine(after is null
            ? "  after:  no standing"
            : $"  after:  rating {after.Value.Rating:F1}, deviation {after.Value.Deviation:F1}, {after.Value.Matches} games");
        return 0;
    }

    public static async Task<int> InspectAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return Fail("usage: laplace inspect <text>");
        CodepointPerfcache.Load(ResolveBlob());

        using var tree = TextDecomposer.Run(text);
        var root = tree.GetNode(tree.NaturalUnitIndex());
        Hash128 id = root.Id;
        Console.WriteLine($"inspect \"{text}\"");
        Console.WriteLine($"  engine-resolved id : {Hex(id)}");
        Console.WriteLine($"  tier {root.Tier}, {tree.NodeCount} nodes in the decomposition DAG\n");

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();

        await using var transaction = await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead);
        bool exists = false;
        {
            var facet = await NpgsqlDisplayLabels.FacetAsync(conn, id.ToBytes(), default);
            if (facet is { } f)
            {
                exists = true;
                Console.WriteLine($"  ENTITY: present  tier={f.Tier}  type={f.Type}");
            }
        }

        var utf8Input = Encoding.UTF8.GetBytes(text);
        var wordSeen = new HashSet<Hash128>();
        var words = new List<(Hash128 Id, string Label)>();
        foreach (var segment in TextDecomposer.WordSegments(utf8Input))
        {
            if (!wordSeen.Add(segment.Id)) continue;
            words.Add((segment.Id, Encoding.UTF8.GetString(utf8Input, segment.Offset, segment.Length)));
        }

        if (!exists)
            Console.WriteLine("  ENTITY: novel composite — correct id, not yet ingested");

        if (words.Count > 1 || !exists)
        {
            Console.WriteLine("\n  CONSTITUENT CONTENT (the substrate answering through the parts it knows):");
            foreach (var (wordId, label) in words)
            {
                var facts = await NpgsqlSubstrateReads.SalientFactsAsync(conn, wordId.ToBytes(), 4, default);
                if (facts.Count > 0)
                {
                    Console.WriteLine($"    \"{label}\"");
                    foreach (var fact in facts)
                        Console.WriteLine($"        [{fact.Type}] → {fact.Fact}  μ={fact.EffMu:F3}  witnesses={fact.Witnesses}");
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
            foreach (var c in await NpgsqlSubstrateReads.ConsensusOutLabeledAsync(conn, id.ToBytes(), 20, default))
            {
                n++;
                Console.WriteLine($"    [{c.TypeLabel}] → {c.ObjectLabel,-28}  μ={c.EffMu:F3}  witnesses={c.Witnesses}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        {
            Console.WriteLine("\n  INCOMING consensus (subject → this), Glicko-2 μ over all witnesses:");
            int n = 0;
            foreach (var c in await NpgsqlSubstrateReads.ConsensusInLabeledAsync(conn, id.ToBytes(), 20, default))
            {
                n++;
                Console.WriteLine($"    {c.SubjectLabel,-28} [{c.TypeLabel}] → here  μ={c.EffMu:F3}  witnesses={c.Witnesses}");
            }
            if (n == 0) Console.WriteLine("    (none)");
        }

        static async Task<int> NativeEvidenceAsync(NpgsqlConnection connection, byte[] entity, bool inbound)
        {
            var rows = await NpgsqlSubstrateReads.EvidenceSeriesReceiptAsync(connection, entity, 40, inbound, default);
            foreach (var e in rows)
            {
                string outcome = e.Games <= 0 ? "no scored records"
                    : e.Score == 0 ? "refutes" : e.Score == e.Games ? "confirms"
                    : $"mean outcome {e.Score / e.Games:F3}";
                Console.WriteLine($"    [{(string.IsNullOrEmpty(e.TypeLabel) ? "value" : e.TypeLabel)}] → {e.ObjectLabel}");
                Console.WriteLine($"      {e.SourceLabel}: {outcome}, records={e.Games:G17}, occurrences={e.Tokens:G17}, trust={e.Trust:G}");
                Console.WriteLine($"      claim={e.ClaimId}  witness={e.SourceId}"
                    + (e.DependenceRoot is null ? "" : $"  dependence={e.DependenceRoot}"));
            }
            return rows.Count;
        }
        {
            Console.WriteLine("\n  OUTGOING evidence (provenance — who witnessed):");
            int n = await NativeEvidenceAsync(conn, id.ToBytes(), false);
            if (n == 0) Console.WriteLine("    (none)");
        }

        {
            Console.WriteLine("\n  INCOMING evidence (provenance — who witnessed):");
            int n = await NativeEvidenceAsync(conn, id.ToBytes(), true);
            if (n == 0) Console.WriteLine("    (none)");
        }

        await transaction.CommitAsync();
        return 0;
    }
}
