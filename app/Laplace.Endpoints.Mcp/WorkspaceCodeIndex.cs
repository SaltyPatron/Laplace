using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.Mcp;

internal static class WorkspaceCodeIndex
{
    internal sealed record CodeChunk(
        string RelativePath,
        string FullPath,
        int Line0,
        int Line1,
        string Symbol,
        string Snippet,
        string EntityIdHex,
        double X,
        double Y,
        double Z,
        double M,
        double[] Trajectory,
        Hash128[] TrajectoryWordIds,
        Hash128[] WordIds,
        HashSet<Hash128> WordIdSet);

    private sealed record FileEntry(
        DateTime LastWriteUtc,
        long Length,
        CodeChunk[] Chunks);

    internal sealed record IndexSnapshot(
        string RootPath,
        DateTime BuiltAtUtc,
        int FileCount,
        CodeChunk[] Chunks,
        Dictionary<Hash128, List<int>> Postings);

    private static readonly Lock SyncLock = new();
    private static Dictionary<string, FileEntry> FileCache = new(StringComparer.OrdinalIgnoreCase);
    private static IndexSnapshot? CurrentSnapshot;
    private static DateTime LastScanCheckUtc = DateTime.MinValue;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".c", ".cpp", ".h", ".hpp", ".sql", ".ts", ".tsx", ".js", ".py", ".ps1", ".sh", ".md"
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "out", ".vs", ".vscode",
        "Laplace-Refactor", "code-search", "packages", "target", "third_party", "vendor",
        "extern", "_deps", "TestResults", "artifacts", "coverage"
    };

    public static string ResolveWorkspaceRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("LAPLACE_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot)) return RequireWorkspaceRoot(envRoot);

        var machineEnv = OperatingSystem.IsWindows()
            ? @"C:\ProgramData\Laplace\machine.env"
            : "/etc/laplace/machine.env";
        if (File.Exists(machineEnv))
        {
            foreach (var line in File.ReadLines(machineEnv))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("LAPLACE_SOURCE_ROOT=", StringComparison.Ordinal))
                {
                    var val = trimmed["LAPLACE_SOURCE_ROOT=".Length..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(val)) return RequireWorkspaceRoot(val);
                }
            }
        }

        if (OperatingSystem.IsWindows() && Directory.Exists(@"E:\Repositories\Laplace"))
            return @"E:\Repositories\Laplace";

        return AppContext.BaseDirectory;
    }

    private static string RequireWorkspaceRoot(string path)
    {
        var root = Path.GetFullPath(path);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
            throw new InvalidDataException($"Declared workspace root is not a directory: {root}");
        return root;
    }

    private readonly record struct WordVector(
        Hash128 Id,
        double X,
        double Y,
        double Z,
        double M,
        double Weight);

    internal readonly record struct TraceResult(
        Hash128 RootId,
        string RootIdHex,
        double Cx,
        double Cy,
        double Cz,
        double Cm,
        double Radius,
        double[] Trajectory,
        Hash128[] TrajectoryWordIds,
        HashSet<Hash128> WordIds,
        Dictionary<Hash128, double> WordWeights,
        int NodeCount);

    internal readonly record struct CandidateScore(
        double RelevanceScore,
        double FrechetDistance,
        double CentroidDistance,
        double WordOverlap,
        int SharedWords);

    private static readonly ConcurrentDictionary<string, WordVector> WordCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Hash128, WordVector> WordByIdCache = new();
    private static readonly ConcurrentDictionary<Hash128, Hash128[]> SemanticPeerCache = new();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "in", "to", "for", "with", "on", "at", "by", "from",
        "as", "is", "are", "was", "were", "be", "been", "or", "and", "not", "this", "that",
        "it", "its", "into", "between", "over", "under", "var", "let", "const"
    };

    internal static string[] ExtractContentWords(string text, int maxWords = 6)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = ExtractOrderedTokens(text, splitIdentifiers: true, includeCompoundIdentifiers: false, allowSingleChar: false);
        var result = new List<string>(maxWords);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tok in tokens)
        {
            if (tok.Length < 3 || StopWords.Contains(tok) || char.IsDigit(tok[0]))
                continue;
            if (seen.Add(tok))
            {
                result.Add(tok);
                if (result.Count >= maxWords)
                    break;
            }
        }
        return result.ToArray();
    }

    internal static IReadOnlyDictionary<Hash128, Hash128[]>? ResolveSemanticPeers(
        NpgsqlDataSource? dataSource,
        IEnumerable<Hash128> wordIds,
        int limitPerWord = 24)
    {
        if (dataSource is null)
            return null;
        return ResolveSemanticPeersAsync(dataSource, wordIds, CancellationToken.None, limitPerWord)
            .GetAwaiter().GetResult();
    }

    internal static async Task<IReadOnlyDictionary<Hash128, Hash128[]>?> ResolveSemanticPeersAsync(
        NpgsqlDataSource? dataSource,
        IEnumerable<Hash128> wordIds,
        CancellationToken ct = default,
        int limitPerWord = 24)
    {
        ct.ThrowIfCancellationRequested();
        if (dataSource is null)
            return null;

        var result = new Dictionary<Hash128, Hash128[]>();
        var missing = new List<Hash128>();
        foreach (var wid in wordIds)
        {
            if (wid == default)
                continue;
            if (WordByIdCache.TryGetValue(wid, out var wv) && wv.Weight < 0.5)
                continue;
            if (SemanticPeerCache.TryGetValue(wid, out var cached))
            {
                if (cached.Length > 0)
                    result[wid] = cached;
            }
            else
            {
                missing.Add(wid);
            }
        }

        if (missing.Count > 0)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                var queryBytes = missing.Select(static id => id.ToBytes()).ToArray();
                var rows = await NpgsqlSubstrateReads.WordPeerIdsBatchAsync(conn, queryBytes, limitPerWord, ct)
                    .ConfigureAwait(false);

                var grouped = new Dictionary<Hash128, List<Hash128>>(missing.Count);
                foreach (var m in missing)
                    grouped[m] = [];

                foreach (var row in rows)
                {
                    if (row.WordId is { Length: 16 } && row.PeerId is { Length: 16 })
                    {
                        var wid = Hash128.FromBytes(row.WordId);
                        var pid = Hash128.FromBytes(row.PeerId);
                        if (grouped.TryGetValue(wid, out var list))
                            list.Add(pid);
                    }
                }

                foreach (var (wid, list) in grouped)
                {
                    var arr = list.ToArray();
                    SemanticPeerCache.TryAdd(wid, arr);
                    if (arr.Length > 0)
                        result[wid] = arr;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Serilog.Log.ForContext(typeof(WorkspaceCodeIndex)).Warning(ex,
                    "MCP workspace peer lookup failed; using cached peers. missing_words={MissingWords} cached_words={CachedWords}",
                    missing.Count, result.Count);
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static double[] ProjectTrajectoryForDoc(
        double[] queryTrajectory,
        Hash128[] queryTrajectoryWordIds,
        HashSet<Hash128> docWordIds,
        IReadOnlyDictionary<Hash128, Hash128[]>? semanticPeers)
    {
        if (semanticPeers is null || semanticPeers.Count == 0 || queryTrajectoryWordIds.Length == 0)
            return queryTrajectory;

        double[]? projected = null;
        var vCount = Math.Min(queryTrajectoryWordIds.Length, queryTrajectory.Length / 4);
        Span<double> slerped = stackalloc double[4];

        for (var i = 0; i < vCount; i++)
        {
            var qWid = queryTrajectoryWordIds[i];
            if (docWordIds.Contains(qWid) || !semanticPeers.TryGetValue(qWid, out var peers))
                continue;

            foreach (var peerId in peers)
            {
                if (docWordIds.Contains(peerId) && WordByIdCache.TryGetValue(peerId, out var peerVec))
                {
                    projected ??= (double[])queryTrajectory.Clone();
                    ReadOnlySpan<double> p0 = queryTrajectory.AsSpan(i * 4, 4);
                    ReadOnlySpan<double> p1 = [peerVec.X, peerVec.Y, peerVec.Z, peerVec.M];
                    Math4d.Slerp(p0, p1, 0.92, slerped);
                    projected[i * 4] = slerped[0];
                    projected[i * 4 + 1] = slerped[1];
                    projected[i * 4 + 2] = slerped[2];
                    projected[i * 4 + 3] = slerped[3];
                    break;
                }
            }
        }

        return projected ?? queryTrajectory;
    }

    internal static TraceResult ComputeTrace(string text)
    {
        CodepointPerfcache.LoadDefault();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TraceResult(
                default,
                new string('0', 32),
                0.0, 0.0, 0.0, 1.0, 0.0,
                [0.0, 0.0, 0.0, 1.0],
                [],
                [],
                [],
                0);
        }

        var rawTrajTokens = ExtractOrderedTokens(text, splitIdentifiers: true, includeCompoundIdentifiers: false, allowSingleChar: true);
        var contentTrajTokens = rawTrajTokens.Where(static t => !StopWords.Contains(t)).ToList();
        var trajTokens = contentTrajTokens.Count > 0 ? contentTrajTokens : rawTrajTokens;
        var allTokens = ExtractOrderedTokens(text, splitIdentifiers: true, includeCompoundIdentifiers: true, allowSingleChar: true);

        const int MaxTrajectoryVertices = 80;
        var rawCount = Math.Min(rawTrajTokens.Count, MaxTrajectoryVertices);
        var vCount = Math.Min(trajTokens.Count, MaxTrajectoryVertices);
        double[] trajectory;
        Hash128[] trajectoryWordIds;
        double cx = 0.0, cy = 0.0, cz = 0.0, cm = 1.0, radius = 0.0;
        Hash128 rootId = default;

        if (vCount > 0)
        {
            var childIds = new Hash128[vCount];
            trajectory = new double[vCount * 4];
            for (var i = 0; i < vCount; i++)
            {
                var wv = GetOrComputeWordVector(trajTokens[i]);
                childIds[i] = wv.Id;
                trajectory[i * 4] = wv.X;
                trajectory[i * 4 + 1] = wv.Y;
                trajectory[i * 4 + 2] = wv.Z;
                trajectory[i * 4 + 3] = wv.M;
            }
            trajectoryWordIds = childIds;

            if (rawCount == 1 && allTokens.Count == 1)
            {
                var single = GetOrComputeWordVector(rawTrajTokens[0]);
                rootId = single.Id;
                cx = single.X;
                cy = single.Y;
                cz = single.Z;
                cm = single.M;
                radius = 1.0;
            }
            else
            {
                var rawChildIds = new Hash128[rawCount];
                var rawCoords = new double[rawCount * 4];
                for (var i = 0; i < rawCount; i++)
                {
                    var wv = GetOrComputeWordVector(rawTrajTokens[i]);
                    rawChildIds[i] = wv.Id;
                    rawCoords[i * 4] = wv.X;
                    rawCoords[i * 4 + 1] = wv.Y;
                    rawCoords[i * 4 + 2] = wv.Z;
                    rawCoords[i * 4 + 3] = wv.M;
                }
                Span<double> outCoord = stackalloc double[4];
                var (id, _) = HashComposer.ComposeNode((byte)EntityTier.Sentence, rawChildIds, rawCoords, outCoord);
                rootId = id;
                Span<double> contentCoord = stackalloc double[4];
                if (vCount == 1)
                {
                    cx = trajectory[0];
                    cy = trajectory[1];
                    cz = trajectory[2];
                    cm = trajectory[3];
                    radius = 1.0;
                }
                else
                {
                    HashComposer.ComposeNode((byte)EntityTier.Sentence, childIds, trajectory, contentCoord);
                    cx = contentCoord[0];
                    cy = contentCoord[1];
                    cz = contentCoord[2];
                    cm = contentCoord[3];
                    var r = Math.Sqrt(cx * cx + cy * cy + cz * cz + cm * cm);
                    radius = r;
                    if (r > 1e-12)
                    {
                        cx /= r;
                        cy /= r;
                        cz /= r;
                        cm /= r;
                    }
                }
            }
        }
        else
        {
            trajectory = [0.0, 0.0, 0.0, 1.0];
            trajectoryWordIds = [];
        }

        var wordIds = new HashSet<Hash128>(allTokens.Count);
        var wordWeights = new Dictionary<Hash128, double>(allTokens.Count);
        foreach (var tok in allTokens)
        {
            var wv = GetOrComputeWordVector(tok);
            wordIds.Add(wv.Id);
            if (!wordWeights.ContainsKey(wv.Id))
                wordWeights[wv.Id] = wv.Weight;
        }

        return new TraceResult(
            rootId,
            Convert.ToHexStringLower(rootId.ToBytes()),
            cx, cy, cz, cm,
            radius,
            trajectory,
            trajectoryWordIds,
            wordIds,
            wordWeights,
            text.Length + allTokens.Count + 1);
    }

    internal static CandidateScore ScoreCandidate(
        in TraceResult queryTrace,
        in TraceResult docTrace,
        string? rawQuery = null,
        string? rawDoc = null,
        IReadOnlyDictionary<Hash128, Hash128[]>? semanticPeers = null)
    {
        if (rawQuery is not null && rawDoc is not null && string.Equals(rawQuery, rawDoc, StringComparison.Ordinal))
        {
            return new CandidateScore(1.0, 0.0, 0.0, 1.0, queryTrace.WordIds.Count);
        }

        if (queryTrace.RootId != default && queryTrace.RootId == docTrace.RootId && queryTrace.Trajectory.Length == docTrace.Trajectory.Length)
        {
            return new CandidateScore(1.0, 0.0, 0.0, 1.0, queryTrace.WordIds.Count);
        }

        var sharedWords = 0;
        var sharedWeight = 0.0;
        var totalWeight = 0.0;
        foreach (var (wid, weight) in queryTrace.WordWeights)
        {
            totalWeight += weight;
            if (docTrace.WordIds.Contains(wid))
            {
                sharedWords++;
                sharedWeight += weight;
            }
            else if (semanticPeers is not null && semanticPeers.TryGetValue(wid, out var peers))
            {
                foreach (var peerId in peers)
                {
                    if (docTrace.WordIds.Contains(peerId))
                    {
                        sharedWords++;
                        sharedWeight += weight * 0.85;
                        break;
                    }
                }
            }
        }

        var overlapRatio = totalWeight > 0.0 ? sharedWeight / totalWeight : 0.0;
        var centroidDist = Math4d.UnitCentroidDistance(
            queryTrace.Cx, queryTrace.Cy, queryTrace.Cz, queryTrace.Cm,
            docTrace.Cx, docTrace.Cy, docTrace.Cz, docTrace.Cm);

        var effectiveQueryTrajectory = ProjectTrajectoryForDoc(
            queryTrace.Trajectory,
            queryTrace.TrajectoryWordIds,
            docTrace.WordIds,
            semanticPeers);
        var frechetDist = Math4d.SubTrajectoryFrechet(effectiveQueryTrajectory, docTrace.Trajectory);
        if (double.IsInfinity(frechetDist) || double.IsNaN(frechetDist))
            frechetDist = centroidDist;

        var frechetSim = 1.0 / (1.0 + frechetDist);
        var centroidSim = 1.0 / (1.0 + centroidDist);

        double score;
        if (sharedWords > 0)
        {
            score = 0.45 * frechetSim + 0.40 * overlapRatio + 0.15 * centroidSim;
        }
        else
        {
            score = 0.20 * frechetSim * centroidSim;
        }

        return new CandidateScore(
            RelevanceScore: Math.Clamp(score, 0.0, 1.0),
            FrechetDistance: frechetDist,
            CentroidDistance: centroidDist,
            WordOverlap: overlapRatio,
            SharedWords: sharedWords);
    }

    public static IReadOnlyList<JsonObject> Search(
        string query,
        string? pathFilter,
        int limit,
        int offset,
        bool forceReindex,
        out int totalIndexedChunks,
        out int totalIndexedFiles,
        IReadOnlyDictionary<Hash128, Hash128[]>? semanticPeers = null)
    {
        var snapshot = EnsureIndex(forceReindex);
        totalIndexedChunks = snapshot.Chunks.Length;
        totalIndexedFiles = FileCache.Count;

        if (snapshot.Chunks.Length == 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var queryVec = ComposeTextVector(query, query);
        var queryTokens = ExtractOrderedTokens(query, splitIdentifiers: true, includeCompoundIdentifiers: true, allowSingleChar: false);
        var normalizedFilter = string.IsNullOrWhiteSpace(pathFilter)
            ? null
            : pathFilter.Replace('\\', '/').Trim('/');

        var nDocs = Math.Max(1, snapshot.Chunks.Length);
        var queryIdf = new Dictionary<Hash128, double>(queryVec.WordIds.Length);
        double totalQueryIdf = 0.0;

        foreach (var wid in queryVec.WordIds)
        {
            if (WordByIdCache.TryGetValue(wid, out var wv) && wv.Weight < 0.5)
                continue;
            var df = snapshot.Postings.TryGetValue(wid, out var list) ? list.Count : 0;
            var idf = Math.Log(1.0 + (nDocs - df + 0.5) / (df + 0.5));
            queryIdf[wid] = idf;
            totalQueryIdf += idf;
        }
        if (queryIdf.Count == 0)
        {
            foreach (var wid in queryVec.WordIds)
            {
                var df = snapshot.Postings.TryGetValue(wid, out var list) ? list.Count : 0;
                var idf = Math.Log(1.0 + (nDocs - df + 0.5) / (df + 0.5));
                queryIdf[wid] = idf;
                totalQueryIdf += idf;
            }
        }
        if (totalQueryIdf <= 0.0)
            totalQueryIdf = 1.0;

        var candidateScores = new Dictionary<int, (double IdfSum, int SharedCount)>();
        var matchedChunksForWord = new HashSet<int>();

        foreach (var (wid, idf) in queryIdf)
        {
            matchedChunksForWord.Clear();
            if (snapshot.Postings.TryGetValue(wid, out var chunkIndices))
            {
                foreach (var idx in chunkIndices)
                {
                    if (normalizedFilter is not null &&
                        !snapshot.Chunks[idx].RelativePath.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    matchedChunksForWord.Add(idx);
                    if (candidateScores.TryGetValue(idx, out var prev))
                        candidateScores[idx] = (prev.IdfSum + idf, prev.SharedCount + 1);
                    else
                        candidateScores[idx] = (idf, 1);
                }
            }

            if (semanticPeers is not null && semanticPeers.TryGetValue(wid, out var peers))
            {
                var peerWeight = idf * 0.85;
                foreach (var peerId in peers)
                {
                    if (!snapshot.Postings.TryGetValue(peerId, out var peerChunkIndices))
                        continue;
                    foreach (var idx in peerChunkIndices)
                    {
                        if (!matchedChunksForWord.Add(idx))
                            continue;
                        if (normalizedFilter is not null &&
                            !snapshot.Chunks[idx].RelativePath.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (candidateScores.TryGetValue(idx, out var prev))
                            candidateScores[idx] = (prev.IdfSum + peerWeight, prev.SharedCount + 1);
                        else
                            candidateScores[idx] = (peerWeight, 1);
                    }
                }
            }
        }

        // Fallback substring scan over symbols/paths if few word-hash postings matched
        if (candidateScores.Count < limit)
        {
            for (var i = 0; i < snapshot.Chunks.Length; i++)
            {
                if (candidateScores.ContainsKey(i))
                    continue;
                var c = snapshot.Chunks[i];
                if (normalizedFilter is not null &&
                    !c.RelativePath.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var hits = 0;
                foreach (var tok in queryTokens)
                {
                    if (c.Symbol.Contains(tok, StringComparison.OrdinalIgnoreCase) ||
                        c.RelativePath.Contains(tok, StringComparison.OrdinalIgnoreCase) ||
                        c.Snippet.Contains(tok, StringComparison.OrdinalIgnoreCase))
                    {
                        hits++;
                    }
                }
                if (hits > 0)
                    candidateScores[i] = (hits * 0.5, hits);
            }
        }

        var stage1 = candidateScores
            .Select(kv =>
            {
                var chunk = snapshot.Chunks[kv.Key];
                var boost = ComputeSymbolAndPathBoost(chunk, query, queryTokens);
                return (Index: kv.Key, Prelim: (kv.Value.IdfSum / totalQueryIdf) + boost * 2.0, kv.Value.IdfSum, kv.Value.SharedCount, Boost: boost);
            })
            .OrderByDescending(static x => x.Prelim)
            .Take(Math.Max(32, (offset + limit) * 3))
            .ToList();

        var results = new List<(double Score, int Index, JsonObject Row)>(stage1.Count);
        foreach (var item in stage1)
        {
            var chunk = snapshot.Chunks[item.Index];
            var centroidDist = Math4d.UnitCentroidDistance(
                queryVec.X, queryVec.Y, queryVec.Z, queryVec.M,
                chunk.X, chunk.Y, chunk.Z, chunk.M);
            var effectiveQueryTrajectory = ProjectTrajectoryForDoc(
                queryVec.Trajectory,
                queryVec.TrajectoryWordIds,
                chunk.WordIdSet,
                semanticPeers);
            var frechetDist = Math4d.SubTrajectoryFrechet(effectiveQueryTrajectory, chunk.Trajectory);
            if (double.IsInfinity(frechetDist) || double.IsNaN(frechetDist))
                frechetDist = centroidDist;

            var idfOverlap = Math.Min(1.0, item.IdfSum / totalQueryIdf);
            var frechetSim = 1.0 / (1.0 + frechetDist);
            var centroidSim = 1.0 / (1.0 + centroidDist);
            var score = Math.Min(1.0, 0.42 * idfOverlap + 0.33 * frechetSim + 0.08 * centroidSim + item.Boost);

            var snippet = chunk.Snippet.Length <= 900
                ? chunk.Snippet
                : chunk.Snippet[..900] + "\n... [truncated]";

            var row = new JsonObject
            {
                ["path"] = chunk.RelativePath,
                ["line0"] = chunk.Line0,
                ["line1"] = chunk.Line1,
                ["symbol"] = chunk.Symbol,
                ["entity_id"] = chunk.EntityIdHex,
                ["relevance_score"] = Math.Round(score, 6),
                ["frechet_distance"] = Math.Round(frechetDist, 6),
                ["centroid_distance"] = Math.Round(centroidDist, 6),
                ["idf_overlap"] = Math.Round(idfOverlap, 6),
                ["shared_words"] = item.SharedCount,
                ["snippet"] = snippet,
            };
            results.Add((score, item.Index, row));
        }

        results.Sort(static (a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
        });

        return results
            .Skip(offset)
            .Take(limit)
            .Select(static r => r.Row)
            .ToList();
    }

    private static double ComputeSymbolAndPathBoost(CodeChunk chunk, string rawQuery, List<string> queryTokens)
    {
        double boost = 0.0;
        var trimmedQuery = rawQuery.Trim();
        var isContinuation = chunk.Symbol.EndsWith(" (cont.)", StringComparison.Ordinal);

        if (trimmedQuery.Length >= 3)
        {
            if (!isContinuation && chunk.Symbol.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                var trimmedSym = chunk.Symbol.AsSpan().Trim();
                var isDefinition = !trimmedSym.StartsWith("[", StringComparison.Ordinal) &&
                                   !trimmedSym.EndsWith(";", StringComparison.Ordinal);
                boost += isDefinition ? 0.095 : 0.065;
            }
            else if (chunk.RelativePath.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                boost += 0.055;
            }
            else if (chunk.Snippet.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                boost += 0.025;
            }
        }

        if (queryTokens.Count > 0)
        {
            var symHits = 0;
            var pathHits = 0;
            foreach (var tok in queryTokens)
            {
                if (!isContinuation && chunk.Symbol.Contains(tok, StringComparison.OrdinalIgnoreCase))
                    symHits++;
                if (chunk.RelativePath.Contains(tok, StringComparison.OrdinalIgnoreCase))
                    pathHits++;
            }
            boost += 0.04 * ((double)symHits / queryTokens.Count);
            boost += 0.02 * ((double)pathHits / queryTokens.Count);
        }

        var isTestPath = chunk.RelativePath.Contains(".Tests/", StringComparison.OrdinalIgnoreCase) ||
                         chunk.RelativePath.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                         chunk.RelativePath.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);

        if (chunk.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            chunk.RelativePath.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ||
            chunk.RelativePath.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ||
            chunk.RelativePath.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ||
            chunk.RelativePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
            chunk.RelativePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            chunk.RelativePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))
        {
            boost += isTestPath ? 0.005 : 0.015;
        }

        return Math.Min(0.17, boost);
    }

    internal static IndexSnapshot EnsureIndex(bool forceReindex)
    {
        var now = DateTime.UtcNow;
        var rootPath = ResolveWorkspaceRoot();
        var existing = CurrentSnapshot;
        if (!forceReindex && existing is not null && existing.RootPath == rootPath
            && (now - LastScanCheckUtc).TotalSeconds < 120)
            return existing;

        lock (SyncLock)
        {
            existing = CurrentSnapshot;
            if (!forceReindex && existing is not null && existing.RootPath == rootPath
                && (now - LastScanCheckUtc).TotalSeconds < 120)
                return existing;

            CodepointPerfcache.LoadDefault();
            var files = DiscoverSourceFiles(rootPath);
            var seenPaths = new HashSet<string>(files.Count, StringComparer.OrdinalIgnoreCase);
            var toIndex = new List<(string FullPath, string RelativePath, DateTime LastWriteUtc, long Length)>();

            foreach (var f in files)
            {
                seenPaths.Add(f.FullPath);
                if (!forceReindex &&
                    FileCache.TryGetValue(f.FullPath, out var cached) &&
                    cached.LastWriteUtc == f.LastWriteUtc &&
                    cached.Length == f.Length)
                {
                    continue;
                }
                toIndex.Add(f);
            }

            var nextCache = new Dictionary<string, FileEntry>(FileCache, StringComparer.OrdinalIgnoreCase);
            var removed = nextCache.Keys.Where(k => !seenPaths.Contains(k)).ToList();
            foreach (var r in removed)
                nextCache.Remove(r);

            if (existing is not null && existing.RootPath == rootPath
                && toIndex.Count == 0 && removed.Count == 0 && !forceReindex)
            {
                LastScanCheckUtc = now;
                return existing;
            }

            if (toIndex.Count > 0)
            {
                var indexedBag = new ConcurrentBag<(string FullPath, FileEntry Entry)>();
                Parallel.ForEach(toIndex, file =>
                {
                    try
                    {
                        var chunks = IndexFile(file.FullPath, file.RelativePath);
                        indexedBag.Add((file.FullPath, new FileEntry(file.LastWriteUtc, file.Length, chunks)));
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.ForContext(typeof(WorkspaceCodeIndex)).Error(ex,
                            "MCP workspace indexing failed for {SourceFile}.", file.FullPath);
                        throw;
                    }
                });

                foreach (var (fullPath, entry) in indexedBag)
                    nextCache[fullPath] = entry;
            }

            var allChunks = new List<CodeChunk>(nextCache.Count * 4);
            foreach (var kv in nextCache.OrderBy(static k => k.Key, StringComparer.OrdinalIgnoreCase))
                allChunks.AddRange(kv.Value.Chunks);

            var chunkArray = allChunks.ToArray();
            var postings = new Dictionary<Hash128, List<int>>(chunkArray.Length * 8);
            for (var i = 0; i < chunkArray.Length; i++)
            {
                foreach (var wid in chunkArray[i].WordIds)
                {
                    if (!postings.TryGetValue(wid, out var list))
                    {
                        list = new List<int>(4);
                        postings[wid] = list;
                    }
                    list.Add(i);
                }
            }

            var snapshot = new IndexSnapshot(rootPath, now, nextCache.Count, chunkArray, postings);
            FileCache = nextCache;
            CurrentSnapshot = snapshot;
            LastScanCheckUtc = now;
            return snapshot;
        }
    }

    internal static List<(string FullPath, string RelativePath, DateTime LastWriteUtc, long Length)> DiscoverSourceFiles(string rootPath)
    {
        var results = new List<(string, string, DateTime, long)>(3000);
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(subDir);
                    if (string.IsNullOrEmpty(name) || name.StartsWith('.') || ExcludedDirectories.Contains(name))
                        continue;
                    stack.Push(subDir);
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(file);
                    if (!AllowedExtensions.Contains(ext))
                        continue;

                    var info = new FileInfo(file);
                    if (info.Length <= 0 || info.Length > 256 * 1024)
                        continue;

                    var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                    results.Add((file, rel, info.LastWriteTimeUtc, info.Length));
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.ForContext(typeof(WorkspaceCodeIndex)).Error(ex,
                    "MCP workspace discovery failed for {SourceDirectory}.", dir);
                throw;
            }
        }

        return results;
    }

    private static CodeChunk[] IndexFile(string fullPath, string relativePath)
    {
        var text = File.ReadAllText(fullPath);
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var rawChunks = SplitIntoDeclarationChunks(lines, relativePath);
        var chunks = new List<CodeChunk>(rawChunks.Count);

        foreach (var (line0, line1, symbol, snippet) in rawChunks)
        {
            if (string.IsNullOrWhiteSpace(snippet))
                continue;

            var isContinuation = symbol.EndsWith(" (cont.)", StringComparison.Ordinal);
            var fullWordText = isContinuation
                ? relativePath + " " + snippet
                : relativePath + " " + symbol + " " + snippet;
            var vec = ComposeTextVector(snippet, fullWordText);

            chunks.Add(new CodeChunk(
                RelativePath: relativePath,
                FullPath: fullPath,
                Line0: line0,
                Line1: line1,
                Symbol: symbol,
                Snippet: snippet,
                EntityIdHex: vec.EntityIdHex,
                X: vec.X,
                Y: vec.Y,
                Z: vec.Z,
                M: vec.M,
                Trajectory: vec.Trajectory,
                TrajectoryWordIds: vec.TrajectoryWordIds,
                WordIds: vec.WordIds,
                WordIdSet: vec.WordIdSet));
        }

        return chunks.ToArray();
    }

    private static List<(int Line0, int Line1, string Symbol, string Snippet)> SplitIntoDeclarationChunks(
        string[] lines,
        string relativePath)
    {
        const int MaxLines = 120;
        var chunks = new List<(int, int, string, string)>();

        var chunkStart = 0;
        var currentSymbol = Path.GetFileName(relativePath);
        var hasCodeInChunk = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();
            var isBoundary = IsDeclarationBoundary(rawLine, trimmed);
            var currentLen = i - chunkStart;

            if (isBoundary)
            {
                var boundaryStart = i;
                while (boundaryStart > chunkStart && IsLeadingCommentOrAttribute(lines[boundaryStart - 1].Trim()))
                {
                    boundaryStart--;
                }

                var flushLen = boundaryStart - chunkStart;
                if (flushLen >= 3 && hasCodeInChunk)
                {
                    var snippet = string.Join('\n', lines, chunkStart, flushLen).Trim();
                    if (!string.IsNullOrWhiteSpace(snippet))
                        chunks.Add((chunkStart + 1, boundaryStart, currentSymbol, snippet));
                    chunkStart = boundaryStart;
                    hasCodeInChunk = false;
                }

                currentSymbol = TrimSymbolHeader(trimmed, relativePath);
                hasCodeInChunk = true;
            }
            else if (currentLen >= MaxLines && !HasBoundaryWithin(lines, i + 1, 25))
            {
                var snippet = string.Join('\n', lines, chunkStart, currentLen).Trim();
                if (!string.IsNullOrWhiteSpace(snippet))
                    chunks.Add((chunkStart + 1, i, currentSymbol, snippet));

                chunkStart = i;
                hasCodeInChunk = false;
                if (!currentSymbol.EndsWith(" (cont.)", StringComparison.Ordinal))
                    currentSymbol += " (cont.)";
            }
            else if (!string.IsNullOrEmpty(trimmed) && !IsLeadingCommentOrAttribute(trimmed))
            {
                hasCodeInChunk = true;
            }
        }

        if (chunkStart < lines.Length)
        {
            var rem = lines.Length - chunkStart;
            var snippet = string.Join('\n', lines, chunkStart, rem).Trim();
            if (!string.IsNullOrWhiteSpace(snippet))
                chunks.Add((chunkStart + 1, lines.Length, currentSymbol, snippet));
        }

        return chunks;
    }

    private static bool HasBoundaryWithin(string[] lines, int startIndex, int lookahead)
    {
        if (startIndex + lookahead >= lines.Length)
            return true;
        var end = Math.Min(lines.Length, startIndex + lookahead);
        for (var j = startIndex; j < end; j++)
        {
            if (IsDeclarationBoundary(lines[j], lines[j].Trim()))
                return true;
        }
        return false;
    }

    private static bool IsLeadingCommentOrAttribute(string trimmed)
    {
        if (string.IsNullOrEmpty(trimmed))
            return false;
        return trimmed.StartsWith("///", StringComparison.Ordinal) ||
               trimmed.StartsWith("//", StringComparison.Ordinal) ||
               trimmed.StartsWith("/*", StringComparison.Ordinal) ||
               trimmed.StartsWith("*", StringComparison.Ordinal) ||
               (trimmed.StartsWith('[') && trimmed.EndsWith(']'));
    }

    private static bool IsDeclarationBoundary(string rawLine, string trimmedLine)
    {
        if (string.IsNullOrEmpty(trimmedLine))
            return false;

        if (trimmedLine.StartsWith("public ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("private ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("internal ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("protected ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("static ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("class ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("struct ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("interface ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("record ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("enum ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("def ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("function ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("export ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("# ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("## ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("### ", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("new(\"", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("app.Map", StringComparison.Ordinal))
        {
            return true;
        }

        // Top-level column-0 C/C++ function or type definitions (e.g. "double math4d_frechet(...) {")
        if (rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]) && char.IsLetter(rawLine[0]))
        {
            if (!trimmedLine.StartsWith("if", StringComparison.Ordinal) &&
                !trimmedLine.StartsWith("for", StringComparison.Ordinal) &&
                !trimmedLine.StartsWith("while", StringComparison.Ordinal) &&
                !trimmedLine.StartsWith("switch", StringComparison.Ordinal) &&
                !trimmedLine.StartsWith("return", StringComparison.Ordinal) &&
                !trimmedLine.StartsWith("typedef", StringComparison.Ordinal) &&
                !trimmedLine.StartsWith("using ", StringComparison.Ordinal) &&
                !trimmedLine.StartsWith("namespace ", StringComparison.Ordinal) &&
                trimmedLine.Contains('(') &&
                !trimmedLine.EndsWith(';'))
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimSymbolHeader(string trimmedLine, string fallback)
    {
        if (string.IsNullOrWhiteSpace(trimmedLine))
            return Path.GetFileName(fallback);
        return trimmedLine.Length <= 120 ? trimmedLine : trimmedLine[..120];
    }

    private static (string EntityIdHex, double X, double Y, double Z, double M, double[] Trajectory, Hash128[] TrajectoryWordIds, Hash128[] WordIds, HashSet<Hash128> WordIdSet) ComposeTextVector(
        string trajectoryText,
        string fullWordText)
    {
        var trajTokens = ExtractOrderedTokens(trajectoryText, splitIdentifiers: true, includeCompoundIdentifiers: false, allowSingleChar: true);
        if (trajTokens.Count == 0)
            trajTokens = ExtractOrderedTokens(trajectoryText, splitIdentifiers: false, includeCompoundIdentifiers: true, allowSingleChar: true);

        const int MaxTrajectoryVertices = 80;
        var vCount = Math.Min(trajTokens.Count, MaxTrajectoryVertices);
        double[] trajectory;
        Hash128[] trajectoryWordIds;
        double cx = 0, cy = 0, cz = 0, cm = 1.0;
        Hash128 rootId = default;

        if (vCount > 0)
        {
            var childIds = new Hash128[vCount];
            trajectory = new double[vCount * 4];
            for (var i = 0; i < vCount; i++)
            {
                var wv = GetOrComputeWordVector(trajTokens[i]);
                childIds[i] = wv.Id;
                trajectory[i * 4] = wv.X;
                trajectory[i * 4 + 1] = wv.Y;
                trajectory[i * 4 + 2] = wv.Z;
                trajectory[i * 4 + 3] = wv.M;
            }
            trajectoryWordIds = childIds;
            Span<double> outCoord = stackalloc double[4];
            var (id, _) = HashComposer.ComposeNode((byte)EntityTier.Sentence, childIds, trajectory, outCoord);
            rootId = id;
            cx = outCoord[0];
            cy = outCoord[1];
            cz = outCoord[2];
            cm = outCoord[3];
            var r = Math.Sqrt(cx * cx + cy * cy + cz * cz + cm * cm);
            if (r > 1e-12)
            {
                cx /= r;
                cy /= r;
                cz /= r;
                cm /= r;
            }
        }
        else
        {
            trajectory = [0.0, 0.0, 0.0, 1.0];
            trajectoryWordIds = [];
        }

        var allTokens = ExtractOrderedTokens(fullWordText, splitIdentifiers: true, includeCompoundIdentifiers: true, allowSingleChar: false);
        var uniqueWordIds = new HashSet<Hash128>(allTokens.Count + vCount);
        foreach (var t in trajTokens)
            uniqueWordIds.Add(GetOrComputeWordVector(t).Id);
        foreach (var t in allTokens)
            uniqueWordIds.Add(GetOrComputeWordVector(t).Id);

        return (
            EntityIdHex: Convert.ToHexStringLower(rootId.ToBytes()),
            X: cx,
            Y: cy,
            Z: cz,
            M: cm,
            Trajectory: trajectory,
            TrajectoryWordIds: trajectoryWordIds,
            WordIds: uniqueWordIds.ToArray(),
            WordIdSet: uniqueWordIds);
    }

    private static WordVector GetOrComputeWordVector(string token)
    {
        if (WordCache.TryGetValue(token, out var cached))
            return cached;

        var recs = CodepointPerfcache.Records;
        Span<Hash128> cpIds = stackalloc Hash128[Math.Min(64, token.Length)];
        Span<double> cpCoords = stackalloc double[cpIds.Length * 4];
        var count = 0;

        foreach (var rune in token.EnumerateRunes())
        {
            if (count >= cpIds.Length)
                break;
            var cp = (uint)rune.Value;
            if (cp < (uint)recs.Length)
            {
                ref readonly var r = ref recs[(int)cp];
                cpIds[count] = r.Hash;
                cpCoords[count * 4] = r.CoordX;
                cpCoords[count * 4 + 1] = r.CoordY;
                cpCoords[count * 4 + 2] = r.CoordZ;
                cpCoords[count * 4 + 3] = r.CoordM;
                count++;
            }
        }

        if (count == 0)
            return default;

        Span<double> outCoord = stackalloc double[4];
        var (id, _) = HashComposer.ComposeNode(
            (byte)EntityTier.Word,
            cpIds[..count],
            cpCoords[..(count * 4)],
            outCoord);

        var x = outCoord[0];
        var y = outCoord[1];
        var z = outCoord[2];
        var m = outCoord[3];
        var radius = Math.Sqrt(x * x + y * y + z * z + m * m);
        if (radius > 1e-12)
        {
            x /= radius;
            y /= radius;
            z /= radius;
            m /= radius;
        }

        var weight = StopWords.Contains(token) || token.Length < 2
            ? 0.12
            : Math.Log(1.0 + token.Length);
        var vec = new WordVector(id, x, y, z, m, weight);
        WordCache.TryAdd(token, vec);
        WordByIdCache.TryAdd(id, vec);
        return vec;
    }

    private static List<string> ExtractOrderedTokens(
        string text,
        bool splitIdentifiers,
        bool includeCompoundIdentifiers = true,
        bool allowSingleChar = false)
    {
        var minLen = allowSingleChar ? 1 : 2;
        var list = new List<string>(32);
        var sb = new StringBuilder();
        var fullIdent = new StringBuilder();
        var subTokenCountInIdent = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetterOrDigit(c) || (!splitIdentifiers && c == '_'))
            {
                if (splitIdentifiers && i > 0 && char.IsUpper(c) && sb.Length > 0 &&
                    (char.IsLower(text[i - 1]) || (i + 1 < text.Length && char.IsLower(text[i + 1]))))
                {
                    if (FlushToken(list, sb, minLen))
                        subTokenCountInIdent++;
                }
                var lower = char.ToLowerInvariant(c);
                sb.Append(lower);
                if (splitIdentifiers && includeCompoundIdentifiers)
                    fullIdent.Append(lower);
            }
            else if (splitIdentifiers && c == '_')
            {
                if (FlushToken(list, sb, minLen))
                    subTokenCountInIdent++;
                if (includeCompoundIdentifiers)
                    fullIdent.Append('_');
            }
            else
            {
                if (FlushToken(list, sb, minLen))
                    subTokenCountInIdent++;
                if (splitIdentifiers && includeCompoundIdentifiers)
                {
                    if (subTokenCountInIdent > 1 && fullIdent.Length >= 3)
                        list.Add(fullIdent.ToString().Trim('_'));
                    fullIdent.Clear();
                }
                subTokenCountInIdent = 0;
            }
        }
        if (FlushToken(list, sb, minLen))
            subTokenCountInIdent++;
        if (splitIdentifiers && includeCompoundIdentifiers && subTokenCountInIdent > 1 && fullIdent.Length >= 3)
            list.Add(fullIdent.ToString().Trim('_'));

        return list;

        static bool FlushToken(List<string> tokens, StringBuilder builder, int minLength)
        {
            if (builder.Length >= minLength)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
                return true;
            }
            builder.Clear();
            return false;
        }
    }
}
