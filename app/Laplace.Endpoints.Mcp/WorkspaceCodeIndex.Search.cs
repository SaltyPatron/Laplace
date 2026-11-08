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

internal static partial class WorkspaceCodeIndex
{
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

    public static JsonObject Reindex(bool force)
    {
        var snapshot = EnsureIndex(force);
        return new JsonObject
        {
            ["root_path"] = snapshot.RootPath,
            ["built_at_utc"] = snapshot.BuiltAtUtc.ToString("o"),
            ["indexed_files"] = FileCache.Count,
            ["indexed_chunks"] = snapshot.Chunks.Length,
            ["unique_words"] = snapshot.Postings.Count,
            ["force"] = force,
        };
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
}
