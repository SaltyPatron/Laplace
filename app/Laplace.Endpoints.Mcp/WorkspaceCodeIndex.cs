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

    /// <summary>The source tree this machine declares as LAPLACE_SRC (process environment, then machine.env).</summary>
    public static string ResolveWorkspaceRoot()
    {
        var declared = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_SRC");
        if (string.IsNullOrWhiteSpace(declared))
            throw new InvalidOperationException(
                "No workspace source root is declared. Set LAPLACE_SRC in the process environment or machine.env.");
        return RequireWorkspaceRoot(declared);
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

        using var tree = TextDecomposer.Run(token);
        var root = tree.GetNode(tree.NaturalUnitIndex());
        var id = root.Id;
        double x, y, z, m;
        unsafe { x = root.Coord[0]; y = root.Coord[1]; z = root.Coord[2]; m = root.Coord[3]; }

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
