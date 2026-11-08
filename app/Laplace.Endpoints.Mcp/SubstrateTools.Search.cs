using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Laplace.Agents;
using Laplace.Chess.Service;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.Mcp;

internal sealed partial class SubstrateTools : IMcpTools
{
    private (string, bool) Ask(JsonObject? args)
    {
        var target = AgentCatalog.Load().Resolve(Opt(args, "model"), Opt(args, "provider"));
        var request = new AgentRequest(
            Req(args, "prompt"),
            Opt(args, "system"),
            OptInt(args, "max_tokens"),
            OptDouble(args, "temperature"));

        // Clamped, not defaulted-and-hoped: a caller asking for 0 seconds gets a
        // call that cannot succeed, and an unbounded one parks this stdio server.
        var timeout = TimeSpan.FromSeconds(Math.Clamp(Int(args, "timeout_seconds", 180), 1, 3600));

        var reply = AgentClient.AskAsync(target, request, timeout).GetAwaiter().GetResult();

        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject
            {
                ["agent"] = reply.Agent,
                ["provider"] = reply.Provider,
                ["model"] = reply.Model,
                ["reply"] = reply.Text,
                // Nullable by design: a metric the provider did not report is a
                // different fact from zero, same rule as the health tool.
                ["finish_reason"] = reply.FinishReason is null ? null : JsonValue.Create(reply.FinishReason),
                ["input_tokens"] = reply.InputTokens is null ? null : JsonValue.Create(reply.InputTokens.Value),
                ["output_tokens"] = reply.OutputTokens is null ? null : JsonValue.Create(reply.OutputTokens.Value),
                ["attempts"] = reply.Attempts,
                ["provider_ms"] = Math.Round(reply.LatencyMs, 1),
                ["note"] = reply.Note is null ? null : JsonValue.Create(reply.Note),
            }),
        };
        return (result.ToJsonString(), false);
    }

    private (string, bool) AgentRoutes()
    {
        var catalog = AgentCatalog.Load();
        var rows = new JsonArray();
        foreach (var d in catalog.Describe())
            rows.Add(new JsonObject
            {
                ["name"] = d.Name,
                ["provider"] = d.Provider,
                ["model"] = d.Model is null ? null : JsonValue.Create(d.Model),
                ["base_url"] = d.BaseUrl,
                ["key_env"] = d.KeyEnv,
                ["credentialed"] = d.Credentialed,
                ["alias"] = d.IsAlias,
                ["default"] = d.IsDefault,
            });

        var result = new JsonObject
        {
            ["rows"] = rows,
            ["config"] = catalog.ConfigPath is null ? null : JsonValue.Create(catalog.ConfigPath),
            ["searched"] = new JsonArray(AgentCatalog.ConfigCandidates()
                .Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["default"] = catalog.DefaultReference() is { } fallback ? JsonValue.Create(fallback) : null,
            ["secret_file"] = AgentProviders.SecretFile,
        };
        return (result.ToJsonString(), false);
    }

    private static (string, bool) Ingest(JsonObject? args)
    {
        var source = Req(args, "source").Trim();
        var path = Req(args, "path").Trim();
        var timeoutSeconds = Int(args, "timeout_seconds", 600);

        // NO LOCAL SOURCE GATE. This used to check `source` against a hand-copied
        // array of CLI keys, which had already drifted: it lacked chess-trajectory,
        // so this tool REJECTED a source the CLI routes fine. A valet over another
        // process must not keep its own copy of that process's menu -- the CLI owns
        // IngestDispatchTable, validates against it, and its error already names
        // every supported source. Forwarding an unknown name costs one process
        // start and returns the authoritative answer instead of a stale one.
        if (!File.Exists(path) && !Directory.Exists(path))
            return ($"path not found: {path}", true);

        return IngestProcessCommand.Run(source, path, timeoutSeconds);
    }

    private static (string, bool) Help(JsonObject? args)
    {
        var name = Opt(args, "name");
        if (name is null)
        {
            var listing = new JsonArray(
                ToolCatalog.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["summary"] = t.Summary }).ToArray());
            return (new JsonObject
            {
                ["binary_path"] = Environment.ProcessPath ?? AppContext.BaseDirectory,
                ["rows"] = listing,
            }.ToJsonString(), false);
        }

        var hit = ToolCatalog.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (hit is null)
            return ($"unknown tool '{name}'. Call help with no arguments for the full list.", true);

        var result = new JsonObject
        {
            ["binary_path"] = Environment.ProcessPath ?? AppContext.BaseDirectory,
            ["rows"] = new JsonArray(new JsonObject
            {
                ["name"] = hit.Name,
                ["description"] = hit.Description,
                ["input_schema"] = hit.BuildSchema(),
            })
        };
        return (result.ToJsonString(), false);
    }

    private static JsonNode? ToJson(object value) => value switch
    {
        DBNull => null,
        bool b => b,
        short s => s,
        int i => i,
        long l => l,
        decimal m => m,
        double d => d,
        float f => f,
        string s => s,
        byte[] bytes => @"\x" + Convert.ToHexStringLower(bytes),
        Array a => new JsonArray([.. a.Cast<object>().Select(ToJson)]),
        _ => value.ToString(),
    };

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (pName, type, description, isRequired) in props)
        {
            var propObj = new JsonObject { ["type"] = type, ["description"] = description };
            if (string.Equals(type, "array", StringComparison.Ordinal))
                propObj["items"] = new JsonObject { ["type"] = "string" };
            properties[pName] = propObj;
            if (isRequired) required.Add(pName);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static string Req(JsonObject? args, string name) =>
        args?[name]?.GetValue<string>()
        ?? throw new ArgumentException($"missing required argument: {name}");

    private static string? Opt(JsonObject? args, string name) => args?[name]?.GetValue<string>();

    /// <summary>
    /// Text half of a text-or-entity tool: either is accepted, but not neither.
    /// Returning null when an entity was supplied selects the entity branch of the
    /// SQL CASE without resolving an empty text value.
    /// </summary>
    private static string? NodeText(JsonObject? args, string name)
    {
        var text = Opt(args, name);
        if (text is not null) return text;
        if (Opt(args, "entity") is not null) return null;
        throw new ArgumentException($"missing required argument: {name} (or entity)");
    }

    private (string, bool) PipelineStatus(JsonObject? args)
    {
        var appDir = Environment.GetEnvironmentVariable("LAPLACE_APP_DIR") ?? "/opt/laplace/app";
        var buildDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build");
        var stampsDir = Path.Combine(buildDir, ".stamps");

        var stampsObj = new JsonObject();
        if (Directory.Exists(stampsDir))
        {
            foreach (var file in Directory.GetFiles(stampsDir, "*.stamp"))
            {
                stampsObj[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file).Trim();
            }
        }

        var result = new JsonObject
        {
            ["app_directory"] = appDir,
            ["app_directory_exists"] = Directory.Exists(appDir),
            ["mcp_binary_exists"] = File.Exists(Path.Combine(appDir, "laplace-mcp")),
            ["uci_binary_exists"] = File.Exists(Path.Combine(appDir, "laplace-uci")),
            ["stamps_directory"] = stampsDir,
            ["stamps"] = stampsObj,
        };

        return (result.ToJsonString(), false);
    }

    private static int Int(JsonObject? args, string name, int fallback) =>
        args?[name]?.GetValue<int>() ?? fallback;

    // Absent and zero are different instructions on the external-agent lane:
    // temperature 0 is a valid request, and max_tokens 0 is not the same as "let
    // the provider decide". A sentinel default would collapse the two.
    private static int? OptInt(JsonObject? args, string name) =>
        args?[name] is { } node ? node.GetValue<int>() : null;

    private static double? OptDouble(JsonObject? args, string name) =>
        args?[name] is { } node ? node.GetValue<double>() : null;

    internal sealed record TraceData(
        Hash128 RootId,
        string RootIdHex,
        double X,
        double Y,
        double Z,
        double M,
        double Radius,
        double[] Trajectory,
        HashSet<Hash128> WordIds,
        double WordWeightSum,
        int NodeCount);

    internal static TraceData ComputeTrace(string text)
    {
        var t = WorkspaceCodeIndex.ComputeTrace(text);
        return new TraceData(
            RootId: t.RootId,
            RootIdHex: t.RootIdHex,
            X: t.Cx, Y: t.Cy, Z: t.Cz, M: t.Cm,
            Radius: t.Radius,
            Trajectory: t.Trajectory,
            WordIds: t.WordIds,
            WordWeightSum: t.WordWeights.Values.Sum(),
            NodeCount: Math.Max(1, t.NodeCount));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe int PerfcacheResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        if (!CodepointPerfcache.TryLookupCodepoint(atom, out var r)) return -1;
        *outId = r.Hash;
        outCoord[0] = r.CoordX;
        outCoord[1] = r.CoordY;
        outCoord[2] = r.CoordZ;
        outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }

    private (string, bool) Embeddings(JsonObject? args)
    {
        var texts = new List<string>();
        if (Opt(args, "input") is { } single && !string.IsNullOrWhiteSpace(single))
            texts.Add(single);
        if (args?["inputs"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    texts.Add(s);
            }
        }
        if (texts.Count == 0)
            throw new ArgumentException("missing required argument: input (or inputs)");

        var includeMeaning = args?["include_meaning"]?.GetValue<bool>() ?? false;
        var meaningLimit = Int(args, "meaning_limit", 10);
        var rows = new List<JsonObject>(texts.Count);

        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            var trace = WorkspaceCodeIndex.ComputeTrace(text);
            var row = new JsonObject
            {
                ["index"] = i,
                ["input"] = text,
                ["entity_id"] = trace.RootIdHex,
                ["embedding"] = new JsonArray(
                    JsonValue.Create(Math.Round(trace.Cx, 8)),
                    JsonValue.Create(Math.Round(trace.Cy, 8)),
                    JsonValue.Create(Math.Round(trace.Cz, 8)),
                    JsonValue.Create(Math.Round(trace.Cm, 8)),
                    JsonValue.Create(Math.Round(trace.Radius, 8))),
                ["vertices"] = Math.Max(1, trace.Trajectory.Length / 4),
                ["words"] = trace.WordIds.Count,
                ["nodes"] = trace.NodeCount,
            };

            if (includeMeaning)
            {
                try
                {
                    using var conn = _dbReadOnly.OpenConnection();
                    var constituentTerms = WorkspaceCodeIndex.ExtractContentWords(text);
                    var lookupRows = NpgsqlSubstrateReads.EmbeddingLookupAsync(
                            conn, text, meaningLimit, includeMeaning: true, default,
                            constituentTerms: constituentTerms)
                        .GetAwaiter().GetResult();
                    var anchor = lookupRows.FirstOrDefault(static r => r.Kind == 0);
                    row["witnessed_in_substrate"] = anchor.EntityId is not null;
                    var meaningArr = new JsonArray();
                    foreach (var m in lookupRows.Where(static r => r.Kind == 1))
                    {
                        meaningArr.Add(new JsonObject
                        {
                            ["relation"] = m.Relation,
                            ["object"] = m.ObjectLabel,
                            ["eff_mu"] = m.EffMu is { } mu ? (double)mu : null,
                            ["witnesses"] = m.Witnesses,
                        });
                    }
                    row["meaning"] = meaningArr;
                }
                catch (NpgsqlException ex)
                {
                    throw new NpgsqlException("Requested embedding meaning lookup failed; no complete result is available.", ex);
                }
            }

            rows.Add(row);
        }

        return JsonRows(rows);
    }

    private (string, bool) Rerank(JsonObject? args)
    {
        var query = Req(args, "query");
        if (args?["documents"] is not JsonArray docNodes || docNodes.Count == 0)
            throw new ArgumentException("missing required argument: documents (non-empty array)");

        var docs = new List<string>(docNodes.Count);
        foreach (var item in docNodes)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                docs.Add(s);
            else if (item is JsonObject obj && obj["text"]?.GetValue<string>() is { } t && !string.IsNullOrWhiteSpace(t))
                docs.Add(t);
        }
        if (docs.Count == 0)
            throw new ArgumentException("documents array contained no non-empty strings");

        var queryTrace = WorkspaceCodeIndex.ComputeTrace(query);
        var semanticPeers = WorkspaceCodeIndex.ResolveSemanticPeers(_dbReadOnly, queryTrace.WordIds);
        var scored = new List<(int Index, double Score, JsonObject Row)>(docs.Count);

        for (var i = 0; i < docs.Count; i++)
        {
            var docText = docs[i];
            var docTrace = WorkspaceCodeIndex.ComputeTrace(docText);
            var s = WorkspaceCodeIndex.ScoreCandidate(queryTrace, docTrace, query, docText, semanticPeers);

            var row = new JsonObject
            {
                ["index"] = i,
                ["relevance_score"] = Math.Round(s.RelevanceScore, 6),
                ["entity_id"] = docTrace.RootIdHex,
                ["frechet_distance"] = Math.Round(s.FrechetDistance, 6),
                ["centroid_distance"] = Math.Round(s.CentroidDistance, 6),
                ["word_overlap"] = Math.Round(s.WordOverlap, 6),
                ["shared_words"] = s.SharedWords,
                ["text"] = docText,
            };
            scored.Add((i, s.RelevanceScore, row));
        }

        scored.Sort(static (a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
        });

        var topN = Int(args, "top_n", scored.Count);
        if (topN > 0 && topN < scored.Count)
            scored.RemoveRange(topN, scored.Count - topN);

        return JsonRows(scored.Select(static s => s.Row));
    }

    private (string, bool) CodeSearch(JsonObject? args)
    {
        var query = Req(args, "query");
        var pathFilter = Opt(args, "path");
        var limit = Math.Clamp(Int(args, "limit", 20), 1, DefaultRowCap);
        var offset = Math.Max(0, Int(args, "offset", 0));
        var forceReindex = args?["reindex"]?.GetValue<bool>() ?? false;

        var queryTrace = WorkspaceCodeIndex.ComputeTrace(query);
        var semanticPeers = WorkspaceCodeIndex.ResolveSemanticPeers(_dbReadOnly, queryTrace.WordIds);

        var workspaceRows = WorkspaceCodeIndex.Search(
            query,
            pathFilter,
            limit,
            offset,
            forceReindex,
            out var indexedChunks,
            out var indexedFiles,
            semanticPeers);

        var combined = new List<JsonObject>(workspaceRows.Count);
        combined.AddRange(workspaceRows);

        if (combined.Count < limit)
        {
            try
            {
                var rootBytes = queryTrace.RootId.ToBytes();
                var memberBytes = queryTrace.WordIds.Select(static id => id.ToBytes()).ToArray();

                using var conn = _dbReadOnly.OpenConnection();
                var page = NpgsqlBrowseReads.NamedEntitiesAsync(
                        conn, memberBytes, rootBytes, offset, Math.Min(limit * 3, DefaultRowCap), 500, default)
                    .GetAwaiter().GetResult();

                var scored = new List<(double Score, JsonObject Row)>(page.Count);
                foreach (var hit in page)
                {
                    double frechetDist = 0.0;
                    double centroidDist = 0.0;
                    double wordOverlap = 0.0;
                    double baseScore = 0.0;

                    if (!string.IsNullOrWhiteSpace(hit.Label))
                    {
                        var hitTrace = WorkspaceCodeIndex.ComputeTrace(hit.Label);
                        var s = WorkspaceCodeIndex.ScoreCandidate(queryTrace, hitTrace, query, hit.Label, semanticPeers);
                        frechetDist = s.FrechetDistance;
                        centroidDist = s.CentroidDistance;
                        wordOverlap = s.WordOverlap;
                        baseScore = s.RelevanceScore;
                    }

                    var exactBonus = string.Equals(hit.MatchKind, "exact", StringComparison.OrdinalIgnoreCase) ? 0.15 : 0.0;
                    var score = Math.Min(1.0, baseScore + exactBonus);

                    scored.Add((score, new JsonObject
                    {
                        ["source"] = "substrate",
                        ["entity_id"] = hit.IdHex,
                        ["label"] = hit.Label,
                        ["tier"] = hit.Tier,
                        ["type"] = hit.Type,
                        ["match_kind"] = hit.MatchKind,
                        ["relevance_score"] = Math.Round(score, 6),
                        ["frechet_distance"] = Math.Round(frechetDist, 6),
                        ["centroid_distance"] = Math.Round(centroidDist, 6),
                        ["word_overlap"] = Math.Round(wordOverlap, 6),
                        ["eff_mu"] = hit.EffMu is { } mu ? (double)mu : null,
                        ["witnesses"] = hit.Witnesses,
                    }));
                }

                scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
                foreach (var item in scored)
                {
                    if (combined.Count >= limit)
                        break;
                    combined.Add(item.Row);
                }
            }
            catch (NpgsqlException ex)
            {
                throw new NpgsqlException("Code search substrate lookup failed; combined results are incomplete.", ex);
            }
        }

        var result = new JsonObject
        {
            ["indexed_files"] = indexedFiles,
            ["indexed_chunks"] = indexedChunks,
            ["rows"] = new JsonArray(combined.Select(static r => (JsonNode)r).ToArray()),
        };
        return (result.ToJsonString(), false);
    }

    private static (string, bool) Reindex(JsonObject? args)
    {
        var force = args?["force"]?.GetValue<bool>() ?? false;
        var summary = WorkspaceCodeIndex.Reindex(force);
        return (new JsonObject
        {
            ["rows"] = new JsonArray(summary),
        }.ToJsonString(), false);
    }
}
