using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Laplace.Agents;
using Laplace.Data;
using Laplace.Engine.Core;
using Laplace.Ops;
using Npgsql;
using Serilog;

namespace Laplace.Mcp;

public sealed partial class SubstrateTools
{
    public sealed record WorkspaceSnapshot(string RootPath, IReadOnlyList<WorkspaceFileEntry> Files)
    {
        public int FileCount => Files.Count;
    }

    public sealed record WorkspaceFileEntry(
        string RelativePath,
        string FullPath,
        string Content,
        double[] Trajectory,
        HashSet<Hash128> Tokens);

    private static readonly Lock WorkspaceIndexLock = new();
    private static WorkspaceSnapshot? _workspaceSnapshot;

    private static readonly HashSet<string> IndexedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".c", ".h", ".cpp", ".hpp", ".sql", ".ts", ".tsx", ".js", ".py", ".ps1", ".sh", ".md", ".recipe",
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", "dist", "target", "packages", "work", "artifacts",
    };

    private (string, bool) Ask(JsonObject? args)
    {
        string prompt = Req(args, "prompt");
        string? agentRef = Opt(args, "agent");
        string? system = Opt(args, "system");
        int? maxTokens = OptInt(args, "max_tokens");
        double? temperature = OptDouble(args, "temperature");

        var catalog = AgentCatalog.Load();
        var target = catalog.Resolve(agentRef ?? catalog.DefaultReference());
        var reply = AgentClient.AskAsync(target, new AgentRequest(prompt, system, maxTokens, temperature), TimeSpan.FromSeconds(120), default)
            .GetAwaiter().GetResult();

        return (new JsonObject
        {
            ["agent"] = reply.Agent,
            ["provider"] = reply.Provider,
            ["model"] = reply.Model,
            ["text"] = reply.Text,
            ["finish_reason"] = reply.FinishReason,
            ["input_tokens"] = reply.InputTokens,
            ["output_tokens"] = reply.OutputTokens,
            ["latency_ms"] = reply.LatencyMs,
            ["attempts"] = reply.Attempts,
        }.ToJsonString(), false);
    }

    private (string, bool) AgentRoutes()
    {
        var catalog = AgentCatalog.Load();
        return JsonRows(catalog.Describe().Select(d => new JsonObject
        {
            ["name"] = d.Name,
            ["provider"] = d.Provider,
            ["model"] = d.Model,
            ["base_url"] = d.BaseUrl,
            ["credentialed"] = d.Credentialed,
            ["is_alias"] = d.IsAlias,
            ["is_default"] = d.IsDefault,
        }));
    }

    private static (string, bool) Ingest(JsonObject? args)
    {
        string source = Req(args, "source");
        string? path = Opt(args, "path");
        int timeoutSeconds = Math.Clamp(Int(args, "timeout_seconds", 600), 1, 7200);
        return IngestProcessCommand.Run(source, path ?? string.Empty, timeoutSeconds, () => IngestProcessRunner.Start(source, path));
    }

    private static (string, bool) Help(JsonObject? args)
    {
        string? name = Opt(args, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            var list = new JsonArray(
                ToolCatalog.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["summary"] = t.Summary }).ToArray());
            return (new JsonObject { ["tools"] = list }.ToJsonString(), false);
        }

        var spec = ToolCatalog.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (spec is null)
            return ($"unknown tool: {name}", true);

        return (new JsonObject
        {
            ["name"] = spec.Name,
            ["summary"] = spec.Summary,
            ["description"] = spec.Description,
            ["inputSchema"] = spec.BuildSchema(),
        }.ToJsonString(), false);
    }

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        float f => float.IsFinite(f) ? JsonValue.Create(f) : JsonValue.Create(f.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        decimal m => JsonValue.Create(m),
        byte[] bytes => JsonValue.Create(Convert.ToHexStringLower(bytes)),
        DateTime dt => JsonValue.Create(dt.ToUniversalTime().ToString("O")),
        DateTimeOffset dto => JsonValue.Create(dto.ToUniversalTime().ToString("O")),
        _ => JsonValue.Create(value.ToString()),
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
        foreach (var (name, type, description, isRequired) in props)
        {
            properties[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = description,
            };
            if (isRequired) required.Add(name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static string Req(JsonObject? args, string name) =>
        Opt(args, name) ?? throw new ArgumentException($"missing required parameter '{name}'");

    private static string? Opt(JsonObject? args, string name) => NodeText(args, name);

    private static string? NodeText(JsonObject? args, string name)
    {
        if (args?[name] is JsonValue val && val.TryGetValue<string>(out var s))
            return s;
        return null;
    }

    private (string, bool) PipelineStatus(JsonObject? _)
    {
        using var conn = _dbReadOnly.OpenConnection();
        var readiness = NpgsqlSubstrateReads.ReadinessInventoryAsync(conn, default).GetAwaiter().GetResult();
        return (new JsonObject
        {
            ["estimated_entities"] = readiness.EstimatedEntities,
            ["estimated_consensus"] = readiness.EstimatedConsensus,
            ["has_entities"] = readiness.HasEntities,
            ["has_consensus"] = readiness.HasConsensus,
            ["missing_native_api"] = new JsonArray(readiness.MissingNativeApi.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            ["missing_native_indexes"] = new JsonArray(readiness.MissingNativeIndexes.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        }.ToJsonString(), false);
    }

    private static int Int(JsonObject? args, string name, int fallback) =>
        OptInt(args, name) ?? fallback;

    private static int? OptInt(JsonObject? args, string name) =>
        args?[name] is JsonValue val && val.TryGetValue<int>(out var v) ? v : null;

    private static double? OptDouble(JsonObject? args, string name) =>
        args?[name] is JsonValue val && val.TryGetValue<double>(out var v) ? v : null;

    private (string, bool) Embeddings(JsonObject? args)
    {
        var inputs = new List<string>();
        if (Opt(args, "input") is { } single && !string.IsNullOrEmpty(single))
            inputs.Add(single);
        if (args?["inputs"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    inputs.Add(s);
            }
        }
        if (inputs.Count == 0)
            return ("embeddings requires 'input' or non-empty 'inputs'", true);

        bool includeMeaning = args?["include_meaning"]?.GetValue<bool>() == true;
        int meaningLimit = Math.Clamp(Int(args, "meaning_limit", 8), 1, 32);

        NpgsqlConnection? conn = includeMeaning ? _dbReadOnly.OpenConnection() : null;
        try
        {
            var results = new JsonArray();
            foreach (var text in inputs)
            {
                var tree = TextDecomposer.Run(Encoding.UTF8.GetBytes(text));
                uint rootIdx = tree.NaturalUnitIndex();
                var root = tree.GetNode(rootIdx);
                double x, y, z, m;
                unsafe
                {
                    x = root.Coord[0];
                    y = root.Coord[1];
                    z = root.Coord[2];
                    m = root.Coord[3];
                }
                double radius = Math.Sqrt(x * x + y * y + z * z + m * m);

                var itemObj = new JsonObject
                {
                    ["input"] = text,
                    ["id"] = root.Id.ToHex(),
                    ["tier"] = root.Tier,
                    ["nodes"] = tree.NodeCount,
                    ["coord"] = new JsonArray(x, y, z, m),
                    ["radius"] = radius,
                };

                if (conn is not null)
                {
                    var segs = TextDecomposer.WordSegments(Encoding.UTF8.GetBytes(text));
                    var utf8 = Encoding.UTF8.GetBytes(text);
                    var words = segs.Select(s => Encoding.UTF8.GetString(utf8, s.Offset, s.Length)).Distinct().ToArray();
                    var lookupRows = NpgsqlSubstrateReads.EmbeddingLookupAsync(conn, text, meaningLimit, true, default, constituentTerms: words)
                        .GetAwaiter().GetResult();
                    var neighbors = new JsonArray();
                    foreach (var nr in lookupRows.Where(r => r.Kind == 1))
                    {
                        neighbors.Add(new JsonObject
                        {
                            ["entity_id"] = nr.EntityId is null ? null : Convert.ToHexStringLower(nr.EntityId),
                            ["relation"] = nr.Relation,
                            ["label"] = nr.ObjectLabel,
                            ["eff_mu"] = nr.EffMu,
                            ["witnesses"] = nr.Witnesses,
                        });
                    }
                    itemObj["meaning"] = neighbors;
                }

                results.Add(itemObj);
            }
            return (new JsonObject { ["rows"] = results }.ToJsonString(), false);
        }
        finally
        {
            conn?.Dispose();
        }
    }

    private (string, bool) Rerank(JsonObject? args)
    {
        string query = Req(args, "query");
        if (args?["documents"] is not JsonArray docsArray || docsArray.Count == 0)
            return ("rerank requires non-empty 'documents' array", true);

        int topN = Math.Clamp(Int(args, "top_n", docsArray.Count), 1, docsArray.Count);
        var qTraj = ExtractTrajectory(query, out var qOrderedIds, out _);
        int qCount = qOrderedIds.Count;

        var scored = new List<(int Index, string Document, double Overlap, double FrechetDistance, double Score)>();
        for (int i = 0; i < docsArray.Count; i++)
        {
            var item = docsArray[i];
            string doc = item is JsonValue v && v.TryGetValue<string>(out var s)
                ? s
                : item is JsonObject obj && obj["text"] is JsonValue tv && tv.TryGetValue<string>(out var t)
                    ? t
                    : string.Empty;
            if (string.IsNullOrEmpty(doc))
            {
                scored.Add((i, doc, 0.0, double.PositiveInfinity, 0.0));
                continue;
            }
            if (string.Equals(query, doc, StringComparison.Ordinal))
            {
                scored.Add((i, doc, 1.0, 0.0, 2.0));
                continue;
            }
            var dTraj = ExtractTrajectory(doc, out _, out var dTokens);
            int matchedQueryTokens = 0;
            for (int qi = 0; qi < qCount; qi++)
            {
                if (dTokens.Contains(qOrderedIds[qi]))
                    matchedQueryTokens++;
            }

            double coverage = qCount > 0 ? (double)matchedQueryTokens / qCount : 0.0;
            double frechet = Math4d.SubTrajectoryFrechet(qTraj, dTraj);
            double proximity = double.IsFinite(frechet) ? 1.0 / (1.0 + Math.Max(0.0, frechet)) : 0.0;
            double score = matchedQueryTokens > 0 ? coverage + proximity : 0.20 * proximity;
            scored.Add((i, doc, coverage, frechet, score));
        }

        return JsonRows(scored
            .OrderByDescending(x => x.Overlap)
            .ThenBy(x => double.IsFinite(x.FrechetDistance) ? x.FrechetDistance : double.MaxValue)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(topN)
            .Select(x => new JsonObject
            {
                ["index"] = x.Index,
                ["score"] = Math.Round(x.Score, 6),
                ["word_overlap"] = Math.Round(x.Overlap, 6),
                ["frechet_distance"] = double.IsFinite(x.FrechetDistance) ? Math.Round(x.FrechetDistance, 6) : null,
                ["document"] = x.Document,
            }));
    }

    private (string, bool) CodeSearch(JsonObject? args)
    {
        string query = Req(args, "query");
        int limit = Math.Clamp(Int(args, "limit", 16), 1, 100);
        string? pathFilter = Opt(args, "path");
        bool includePeers = args?["include_peers"]?.GetValue<bool>() == true;

        var qTraj = ExtractTrajectory(query, out _, out var qTokens);
        var hits = new List<JsonObject>();

        // Prefer the ingested PostgreSQL substrate containment/AST index over working-tree disk scanning.
        try
        {
            var termBytes = qTokens
                .Where(static id => id != Hash128.Zero)
                .Take(64)
                .Select(static id => id.ToBytes())
                .ToArray();
            var files = termBytes.Length > 0
                ? NpgsqlSubstrateReads.SearchCodeFilesAsync(_dbReadOnly, termBytes, pathFilter, limit, 0, default)
                    .GetAwaiter().GetResult()
                : [];
            if (files.Count > 0)
            {
                var rankedFiles = files
                    .Select(sym =>
                    {
                        string fileName = Path.GetFileName(sym.FilePath);
                        string docText = $"{fileName}\n{sym.Snippet}";
                        var dTraj = ExtractTrajectory(docText, out _, out var dTokens);
                        double overlap = TokenOverlap(qTokens, dTokens);
                        bool exact = sym.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || sym.Snippet.Contains(query, StringComparison.OrdinalIgnoreCase);
                        double frechet = qTraj.Length > 0 && dTraj.Length > 0
                            ? Math4d.SubTrajectoryFrechet(qTraj, dTraj)
                            : double.PositiveInfinity;
                        double score = (exact ? 1.0 : 0.0) + overlap;
                        return (Row: sym, Name: fileName, Overlap: overlap, Frechet: frechet, Exact: exact, Score: score);
                    })
                    .OrderByDescending(x => x.Exact)
                    .ThenByDescending(x => x.Overlap)
                    .ThenBy(x => double.IsFinite(x.Frechet) ? x.Frechet : double.MaxValue)
                    .Take(limit)
                    .ToArray();

                foreach (var hit in rankedFiles)
                {
                    hits.Add(new JsonObject
                    {
                        ["path"] = hit.Row.FilePath,
                        ["kind"] = $"tier-{hit.Row.UnitTier}",
                        ["name"] = hit.Name,
                        ["snippet"] = hit.Row.Snippet,
                        ["score"] = Math.Round(hit.Score, 6),
                        ["word_overlap"] = Math.Round(hit.Overlap, 6),
                        ["frechet_distance"] = double.IsFinite(hit.Frechet) ? Math.Round(hit.Frechet, 6) : null,
                    });
                }

                if (includePeers && qTokens.Count > 0)
                {
                    var peers = ResolveSemanticPeersOptionalAsync(_dbReadOnly, qTokens.Take(8), default).GetAwaiter().GetResult();
                    if (peers is not null)
                    {
                        foreach (var kv in peers.Take(limit))
                        {
                            hits.Add(new JsonObject
                            {
                                ["word_id"] = kv.Key.ToHex(),
                                ["peers"] = new JsonArray(kv.Value.Select(p => (JsonNode)JsonValue.Create(p.ToHex())!).ToArray()),
                                ["kind"] = "peer",
                            });
                        }
                    }
                }

                return (new JsonObject
                {
                    ["source"] = "substrate",
                    ["files_indexed"] = rankedFiles.Select(r => r.Row.FileIdHex).Distinct(StringComparer.Ordinal).Count(),
                    ["rows"] = new JsonArray(hits.Take(limit).ToArray()),
                }.ToJsonString(), false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.ForContext<SubstrateTools>().Debug(ex, "Substrate code search unavailable; falling back to local workspace index.");
        }

        var snapshot = EnsureIndex(forceReindex: false);

        var ranked = snapshot.Files
            .Where(f => string.IsNullOrWhiteSpace(pathFilter)
                || f.RelativePath.Contains(pathFilter, StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                double overlap = TokenOverlap(qTokens, f.Tokens);
                bool substr = f.Content.Contains(query, StringComparison.OrdinalIgnoreCase);
                double frechet = qTraj.Length > 0 && f.Trajectory.Length > 0
                    ? Math4d.SubTrajectoryFrechet(qTraj, f.Trajectory)
                    : double.PositiveInfinity;
                double proximity = double.IsFinite(frechet) ? 0.5 / (1.0 + Math.Max(0.0, frechet)) : 0.0;
                double score = (substr ? 2.0 : 0.0) + overlap + proximity;
                return (File: f, Overlap: overlap, Frechet: frechet, Score: score, Substr: substr);
            })
            .Where(x => x.Substr || x.Overlap > 0.0 || x.Score > 0.25)
            .OrderByDescending(x => x.Substr)
            .ThenByDescending(x => x.Overlap)
            .ThenBy(x => double.IsFinite(x.Frechet) ? x.Frechet : double.MaxValue)
            .Take(limit)
            .ToArray();

        foreach (var hit in ranked)
        {
            hits.Add(new JsonObject
            {
                ["path"] = hit.File.RelativePath,
                ["score"] = Math.Round(hit.Score, 6),
                ["word_overlap"] = Math.Round(hit.Overlap, 6),
                ["frechet_distance"] = double.IsFinite(hit.Frechet) ? Math.Round(hit.Frechet, 6) : null,
                ["kind"] = "file",
            });
        }

        if (includePeers && qTokens.Count > 0)
        {
            var peers = ResolveSemanticPeersOptionalAsync(_dbReadOnly, qTokens.Take(8), default).GetAwaiter().GetResult();
            if (peers is not null)
            {
                foreach (var kv in peers.Take(limit))
                {
                    hits.Add(new JsonObject
                    {
                        ["word_id"] = kv.Key.ToHex(),
                        ["peers"] = new JsonArray(kv.Value.Select(p => (JsonNode)JsonValue.Create(p.ToHex())!).ToArray()),
                        ["kind"] = "peer",
                    });
                }
            }
        }

        return (new JsonObject
        {
            ["source"] = "workspace-fallback",
            ["root"] = snapshot.RootPath,
            ["files_indexed"] = snapshot.FileCount,
            ["rows"] = new JsonArray(hits.Take(limit).ToArray()),
        }.ToJsonString(), false);
    }

    private static (string, bool) Reindex(JsonObject? args)
    {
        bool force = args?["force"]?.GetValue<bool>() == true;
        var snapshot = EnsureIndex(force);
        return (new JsonObject
        {
            ["root"] = snapshot.RootPath,
            ["file_count"] = snapshot.FileCount,
        }.ToJsonString(), false);
    }

    public static string ResolveWorkspaceRoot()
    {
        var configured = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_SRC");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("LAPLACE_SRC is not configured; cannot resolve workspace source root.");

        var candidate = configured.Trim();
        if (File.Exists(candidate))
            throw new InvalidDataException($"LAPLACE_SRC '{candidate}' points to a file instead of a directory.");
        if (!Directory.Exists(candidate))
            throw new FileNotFoundException($"Workspace source directory '{candidate}' does not exist.", candidate);
        return Path.GetFullPath(candidate);
    }

    public static IReadOnlyList<string> DiscoverSourceFiles(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Workspace directory not found: '{root}'.");

        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(root));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (!SkippedDirectories.Contains(name))
                    stack.Push(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (IndexedExtensions.Contains(ext))
                    files.Add(file);
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    public static WorkspaceSnapshot EnsureIndex(bool forceReindex = false)
    {
        string root = ResolveWorkspaceRoot();
        lock (WorkspaceIndexLock)
        {
            if (!forceReindex && _workspaceSnapshot is { } existing
                && string.Equals(existing.RootPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            var paths = DiscoverSourceFiles(root);
            var entries = new WorkspaceFileEntry[paths.Count];
            var errors = new ConcurrentQueue<Exception>();

            Parallel.For(0, paths.Count, i =>
            {
                string path = paths[i];
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    string content = reader.ReadToEnd();
                    string sample = content.Length <= 4096 ? content : content[..4096];
                    double[] traj = string.IsNullOrWhiteSpace(sample) ? [] : ExtractTrajectory(sample, out _, out _);
                    HashSet<Hash128> tokens = ExtractWordHashes(sample);
                    entries[i] = new WorkspaceFileEntry(
                        Path.GetRelativePath(root, path).Replace('\\', '/'),
                        path,
                        content,
                        traj,
                        tokens);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.ForContext<SubstrateTools>().Error(ex, "MCP workspace indexing failed for {FilePath}.", path);
                    errors.Enqueue(ex);
                }
            });

            if (!errors.IsEmpty)
                throw new AggregateException("One or more workspace source files failed during indexing.", errors);

            var snapshot = new WorkspaceSnapshot(root, entries);
            _workspaceSnapshot = snapshot;
            return snapshot;
        }
    }

    public static async Task<IReadOnlyDictionary<Hash128, Hash128[]>?> ResolveSemanticPeersOptionalAsync(
        NpgsqlDataSource? dataSource,
        IEnumerable<Hash128> wordIds,
        CancellationToken ct = default)
    {
        try
        {
            return await NpgsqlSubstrateReads.ResolveSemanticPeersAsync(dataSource, wordIds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.ForContext<SubstrateTools>().Warning(ex, "MCP workspace peer lookup failed.");
            return null;
        }
    }

    private static List<string> ExtractSubTokens(string text)
    {
        var list = new List<string>(32);
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsLetterOrDigit(c))
            {
                if (i > 0 && char.IsUpper(c) && sb.Length > 0
                    && (char.IsLower(text[i - 1]) || (i + 1 < text.Length && char.IsLower(text[i + 1]))))
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                list.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            list.Add(sb.ToString());
        return list;
    }

    private static double[] ExtractTrajectory(string text, out List<Hash128> orderedIds, out HashSet<Hash128> tokens)
    {
        orderedIds = [];
        tokens = [];
        if (string.IsNullOrWhiteSpace(text)) return [];

        const int MaxVertices = 80;
        var subTokens = ExtractSubTokens(text);
        if (subTokens.Count > 0)
        {
            int vertCount = Math.Min(subTokens.Count, MaxVertices);
            var coords = new double[vertCount * 4];
            orderedIds = new List<Hash128>(vertCount);
            for (int i = 0; i < subTokens.Count; i++)
            {
                using var sub = TextDecomposer.Run(Encoding.UTF8.GetBytes(subTokens[i]));
                var node = sub.GetNode(sub.NaturalUnitIndex());
                tokens.Add(node.Id);
                if (i < vertCount)
                {
                    orderedIds.Add(node.Id);
                    unsafe
                    {
                        coords[i * 4 + 0] = node.Coord[0];
                        coords[i * 4 + 1] = node.Coord[1];
                        coords[i * 4 + 2] = node.Coord[2];
                        coords[i * 4 + 3] = node.Coord[3];
                    }
                }
            }
            return coords;
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var segments = TextDecomposer.WordSegments(utf8);
        if (segments.Length > 0)
        {
            int vertCount = Math.Min(segments.Length, MaxVertices);
            var coords = new double[vertCount * 4];
            orderedIds = new List<Hash128>(vertCount);
            for (int i = 0; i < segments.Length; i++)
            {
                tokens.Add(segments[i].Id);
                if (i < vertCount)
                {
                    orderedIds.Add(segments[i].Id);
                    string word = Encoding.UTF8.GetString(utf8, segments[i].Offset, segments[i].Length);
                    using var sub = TextDecomposer.Run(Encoding.UTF8.GetBytes(word));
                    var node = sub.GetNode(sub.NaturalUnitIndex());
                    unsafe
                    {
                        coords[i * 4 + 0] = node.Coord[0];
                        coords[i * 4 + 1] = node.Coord[1];
                        coords[i * 4 + 2] = node.Coord[2];
                        coords[i * 4 + 3] = node.Coord[3];
                    }
                }
            }
            return coords;
        }

        using var tree = TextDecomposer.Run(utf8);
        var root = tree.GetNode(tree.NaturalUnitIndex());
        tokens.Add(root.Id);
        orderedIds.Add(root.Id);
        unsafe { return [root.Coord[0], root.Coord[1], root.Coord[2], root.Coord[3]]; }
    }

    private static HashSet<Hash128> ExtractWordHashes(string text)
    {
        var tokens = new HashSet<Hash128>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;
        var subTokens = ExtractSubTokens(text);
        if (subTokens.Count > 0)
        {
            foreach (var tok in subTokens)
                if (TextDecomposer.ContentRootId(tok) is { } id)
                    tokens.Add(id);
            return tokens;
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        foreach (var seg in TextDecomposer.WordSegments(utf8))
            tokens.Add(seg.Id);
        return tokens;
    }

    private static double TokenOverlap(HashSet<Hash128> queryTokens, HashSet<Hash128> docTokens)
    {
        if (queryTokens.Count == 0 || docTokens.Count == 0) return 0.0;
        int hits = 0;
        foreach (var id in queryTokens)
            if (docTokens.Contains(id)) hits++;
        return (double)hits / Math.Max(1, queryTokens.Count);
    }
}
