using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Laplace.Agents;
using Laplace.Data;
using Laplace.Engine.Core;
using Laplace.Ops;

namespace Laplace.Mcp;

public sealed partial class SubstrateTools
{
    private (string, bool) Bubble(JsonObject? args)
    {
        string term = Req(args, "term");
        int k = Math.Clamp(Int(args, "k", 12), 1, 100);
        var id = NpgsqlSubstrateReads.ResolveRefAsync(_dbReadOnly, term, default).GetAwaiter().GetResult()
            ?? Hash128.Text(term).ToBytes();

        using var conn = _dbReadOnly.OpenConnection();
        var senses = NpgsqlSubstrateReads.SensesAsync(conn, id, default).GetAwaiter().GetResult();
        if (senses.Count > 0)
        {
            return JsonRows(senses.Take(k).Select(r => new JsonObject
            {
                ["sense_id"] = r.SenseIdHex,
                ["synset_id"] = r.SynsetIdHex,
                ["synset"] = r.SynsetLabel,
                ["eff_mu"] = r.EffMu,
                ["witnesses"] = r.Witnesses,
            }));
        }

        var mesh = NpgsqlSubstrateReads.MeshPositionAsync(_dbReadOnly, id, k, k, default)
            .GetAwaiter().GetResult();
        return JsonRows(mesh.Select(r => new JsonObject
        {
            ["dir"] = r.Dir,
            ["entity_id"] = r.IdHex,
            ["label"] = r.Label,
            ["relation"] = r.Relation,
            ["eff_mu"] = r.EffMu,
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) Api(JsonObject? args)
    {
        string query = Opt(args, "query") ?? string.Empty;
        var rows = NpgsqlSubstrateReads.ApiCatalogAsync(_dbReadOnly, query, default).GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["name"] = r.Name,
            ["args"] = r.Args,
            ["returns"] = r.Returns,
            ["kind"] = r.Kind,
        }), 512);
    }

    private (string, bool) Op(JsonObject? args)
    {
        string name = Req(args, "name");
        if (InstalledOpInvoker.IsWritable(name))
            return ($"refusing writable op '{name}' via MCP read lane", true);

        var namedArgs = args?["args"] as JsonObject;
        IReadOnlyDictionary<string, JsonNode?>? dictArgs = namedArgs is null
            ? null
            : namedArgs.ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.Ordinal);
        int maxRows = Math.Clamp(Int(args, "max_rows", InstalledOpInvoker.DefaultRowCap), 1, 2000);
        int timeoutSeconds = Math.Clamp(Int(args, "timeout_seconds", InstalledOpInvoker.DefaultCommandTimeoutSeconds), 1, 120);
        var result = InstalledOpInvoker.InvokeAsync(_dbReadOnly, name, dictArgs, maxRows, timeoutSeconds, default)
            .GetAwaiter().GetResult();
        if (!string.IsNullOrEmpty(result.Error))
            return (result.Error, true);

        var rows = new JsonArray();
        foreach (var dict in result.Rows)
        {
            var obj = new JsonObject();
            foreach (var kv in dict)
                obj[kv.Key] = ToJson(kv.Value);
            rows.Add(obj);
        }
        return (new JsonObject
        {
            ["rows"] = rows,
            ["truncated_at"] = result.TruncatedAt,
        }.ToJsonString(), false);
    }

    private (string, bool) Translate(JsonObject? args)
    {
        string term = Req(args, "term");
        int limit = Math.Clamp(Int(args, "limit", 24), 1, 100);
        var rows = NpgsqlSubstrateReads.TranslationsAsync(_dbReadOnly, term, limit, default).GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["translation"] = r.Translation,
            ["language"] = r.Language,
            ["eff_mu"] = r.EffMu,
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) Walk(JsonObject? args)
    {
        string? prompt = Opt(args, "prompt");
        string? entityHex = Opt(args, "entity");
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(entityHex))
            return ("walk requires 'prompt' or 'entity'", true);

        string? relation = Opt(args, "relation_type");
        int depth = Math.Clamp(Int(args, "depth", 3), 1, 8);
        int breadth = Math.Clamp(Int(args, "breadth", 8), 1, 64);

        var rows = NpgsqlSubstrateReads.WalkBranchesAsync(_dbReadOnly, prompt, entityHex, relation, depth, breadth, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["depth"] = r.Depth,
            ["path"] = r.Path,
            ["eff_mu"] = r.EffMu,
            ["path_mu"] = r.PathMu,
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) Health()
    {
        var health = NpgsqlSubstrateReads.SubstrateHealthAsync(_dbReadOnly, default).GetAwaiter().GetResult();
        var counts = NpgsqlSubstrateReads.SubstrateCountsAsync(_dbReadOnly, default).GetAwaiter().GetResult();
        var root = new JsonObject();
        foreach (var h in health) root[h.Metric] = h.Value;
        var countObj = new JsonObject();
        foreach (var c in counts) countObj[c.Metric] = c.Value;
        root["counts"] = countObj;
        return (root.ToJsonString(), false);
    }

    private static (string, bool) McpRuntime()
    {
        var obj = new JsonObject
        {
            ["process_id"] = Environment.ProcessId,
            ["binary"] = Environment.ProcessPath ?? AppContext.BaseDirectory,
            ["framework"] = Environment.Version.ToString(),
            ["machine"] = Environment.MachineName,
        };
        return (obj.ToJsonString(), false);
    }

    private (string, bool) SourceStatus(JsonObject? args)
    {
        string? source = Opt(args, "source");
        var rows = NpgsqlSubstrateReads.SourceStatusAsync(_dbReadOnly, source, default).GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["source"] = r.Source,
            ["source_id"] = Convert.ToHexStringLower(r.SourceId),
            ["known"] = r.Known,
            ["ingested"] = r.Ingested,
            ["evidence"] = r.EvidenceApprox,
            ["has_entities"] = r.HasEntities,
        }));
    }

    private static (string, bool) JsonRows(IEnumerable<JsonObject> rows, int cap = DefaultRowCap)
    {
        var arr = new JsonArray();
        int n = 0;
        foreach (var row in rows)
        {
            if (n >= cap) break;
            arr.Add(row);
            n++;
        }
        return (new JsonObject { ["rows"] = arr, ["count"] = n }.ToJsonString(), false);
    }

    private (string, bool) ChatTurn(JsonObject? args)
    {
        string prompt = Req(args, "prompt");
        string session = Opt(args, "session") ?? _processSessionKey;
        string participant = Opt(args, "user") ?? McpTenant;
        bool readOnly = args?["read_only"]?.GetValue<bool>() == true;
        return TurnAsync(prompt, session, participant, readOnly);
    }

    private (string, bool) WitnessFact(JsonObject? args)
    {
        string text = Req(args, "text");
        var workDir = Path.Combine(Path.GetTempPath(), "laplace-mcp-witness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            string notePath = Path.Combine(workDir, "note.txt");
            File.WriteAllText(notePath, text, new UTF8Encoding(false));
            var (stdout, stderr) = RunNativeWitness(workDir, [notePath]);
            return (new JsonObject
            {
                ["witness"] = McpTenant,
                ["trust_class"] = FeedbackTrustClass,
                ["content_id"] = Hash128.Text(text).ToHex(),
                ["stdout"] = stdout,
                ["stderr"] = stderr,
            }.ToJsonString(), false);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private (string, bool) Feedback(JsonObject? args)
    {
        string verdict = (Opt(args, "verdict") ?? string.Empty).Trim().ToLowerInvariant();
        if (verdict is not ("confirm" or "refute"))
            return ("feedback verdict must be 'confirm' or 'refute'", true);

        string subject = Opt(args, "subject")?.Trim() ?? string.Empty;
        string relation = Opt(args, "relation")?.Trim() ?? string.Empty;
        string obj = Opt(args, "object")?.Trim() ?? string.Empty;
        if (subject.Length == 0 || relation.Length == 0)
            return ("feedback names one claim: subject and relation, and object unless the claim has none", true);

        static string Esc(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);

        var before = NpgsqlClaimStanding.ReadAsync(_dbReadOnly, subject, relation, obj, default).GetAwaiter().GetResult();
        string score = verdict == "confirm" ? "1.0" : "0.0";
        string line = string.Create(CultureInfo.InvariantCulture, $"{Esc(subject)}\t{Esc(relation)}\t{Esc(obj)}\t{score}\n");

        var workDir = Path.Combine(Path.GetTempPath(), "laplace-mcp-feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            string stmtPath = Path.Combine(workDir, "feedback.statement");
            File.WriteAllText(stmtPath, line, new UTF8Encoding(false));
            RunNativeWitness(workDir, [stmtPath]);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }

        var after = NpgsqlClaimStanding.ReadAsync(_db, subject, relation, obj, default).GetAwaiter().GetResult();

        static JsonObject? State(NpgsqlClaimStanding.Row? s) => s is null ? null : new JsonObject
        {
            ["claim_id"] = s.Value.Claim.ToHex(),
            ["rating"] = s.Value.Rating,
            ["rd"] = s.Value.Deviation,
            ["matches"] = s.Value.Matches,
            ["confidence"] = s.Value.Confidence,
        };

        return (new JsonObject
        {
            ["verdict"] = verdict,
            ["witness"] = McpTenant,
            ["before"] = State(before),
            ["after"] = State(after),
        }.ToJsonString(), false);
    }

    private (string Stdout, string Stderr) RunNativeWitness(string rootDir, IReadOnlyList<string> files)
    {
        string exe = ResolveNativeBinary()
            ?? throw new InvalidOperationException("Native C engine executable ('laplace') not found; set LAPLACE_ENGINE_EXE or LAPLACE_BUILD.");

        string connInfo = string.IsNullOrWhiteSpace(_explicitConnectionString)
            ? LaplaceEngineRuntime.ResolveLibpqConnInfo()
            : ToLibpqConnInfo(_explicitConnectionString);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("witness");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(connInfo);
        psi.ArgumentList.Add("--as");
        psi.ArgumentList.Add(McpTenant);
        psi.ArgumentList.Add("--class");
        psi.ArgumentList.Add(FeedbackTrustClass);
        psi.ArgumentList.Add("--lineage");
        psi.ArgumentList.Add(McpTenant);
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(rootDir);
        foreach (var file in files)
            psi.ArgumentList.Add(file);

        using var proc = CapturedProcess.Start(psi);
        var result = proc.Completion.GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            throw new InvalidOperationException($"Native witness command exited with code {result.ExitCode}: {detail.Trim()}");
        }
        return (result.Stdout, result.Stderr);
    }

    private static string ToLibpqConnInfo(string npgsqlConn)
        => LaplaceEngineRuntime.FormatLibpqConnInfo(npgsqlConn);

    private static string? ResolveNativeBinary()
    {
        var explicitExe = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_EXE")
            ?? LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_BIN");
        var exeName = OperatingSystem.IsWindows() ? "laplace.exe" : "laplace";
        if (!string.IsNullOrWhiteSpace(explicitExe))
            return File.Exists(explicitExe.Trim()) ? Path.GetFullPath(explicitExe.Trim()) : null;

        foreach (var probe in new[]
        {
            Path.Combine(AppContext.BaseDirectory, exeName),
            Path.Combine(AppContext.BaseDirectory, "bin", exeName),
        })
            if (File.Exists(probe)) return Path.GetFullPath(probe);

        var declaredBuild = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_BUILD");
        if (!string.IsNullOrWhiteSpace(declaredBuild))
        {
            foreach (var sub in new[]
            {
                Path.Combine("Laplace-Engine", "icx-release", exeName),
                Path.Combine("Laplace-Engine", "icx-release", "src", exeName),
            })
            {
                var candidate = Path.Combine(declaredBuild.Trim(), sub);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        if (LaplaceInstall.TryDefaultBuildRoot(out var buildRoot))
        {
            foreach (var sub in new[]
            {
                Path.Combine("Laplace-Engine", "icx-release", exeName),
                Path.Combine("Laplace-Engine", "icx-release", "src", exeName),
                Path.Combine("icx-release", exeName),
            })
            {
                var candidate = Path.Combine(buildRoot, sub);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private ExternalAgentClient AgentClient => _agents ??= new ExternalAgentClient();
}
