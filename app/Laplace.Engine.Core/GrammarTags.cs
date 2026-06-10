namespace Laplace.Engine.Core;

/// <summary>
/// Runs a grammar's tags.scm def/ref query over source (the sealed engine mechanism) and resolves
/// the .scm source from the vendored grammar. Used to turn code into typed CALLS/DEFINES/REFERENCES
/// arcs (richer than positional PRECEDES). No tree-sitter type crosses this surface.
/// </summary>
public static unsafe class GrammarTags
{
    public static IReadOnlyList<TagCapture> Run(IntPtr recipe, ReadOnlySpan<byte> tagsScm, ReadOnlySpan<byte> utf8)
    {
        if (recipe == IntPtr.Zero || tagsScm.IsEmpty || utf8.IsEmpty)
            return Array.Empty<TagCapture>();

        LaplaceTag* outTags = null;
        nuint n = 0;
        int rc;
        fixed (byte* t = tagsScm)
        fixed (byte* u = utf8)
        {
            rc = NativeInterop.GrammarTagsRun(recipe, t, (nuint)tagsScm.Length, u, (nuint)utf8.Length, &outTags, &n);
        }
        if (rc != 0 || outTags == null) return Array.Empty<TagCapture>();

        try
        {
            var list = new List<TagCapture>((int)n);
            for (nuint i = 0; i < n; i++)
            {
                LaplaceTag g = outTags[i];
                list.Add(new TagCapture(g.MatchId, (TagType)g.CaptureType, g.StartByte, g.EndByte));
            }
            return list;
        }
        finally
        {
            NativeInterop.GrammarTagsFree(outTags);
        }
    }

    // --- tags.scm source resolution (from the vendored grammars; cached) ---

    private static readonly Dictionary<string, byte[]?> _cache = new();

    /// <summary>The grammar's tags.scm bytes for a modality, or null if it ships none.</summary>
    public static byte[]? TagsSource(string modality)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(modality, out var cached)) return cached;
            var path = LocateTagsScm(modality);
            byte[]? bytes = path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
            _cache[modality] = bytes;
            return bytes;
        }
    }

    // Grammars with non-standard layouts: sub-parser directory between repo root and queries/.
    private static readonly Dictionary<string, string> _repoSubpath = new(StringComparer.OrdinalIgnoreCase)
    {
        ["typescript"] = "typescript",
        ["php"]        = "php",
    };

    private static string? LocateTagsScm(string modality)
    {
        _repoSubpath.TryGetValue(modality, out var sub);
        // The CLI often runs from a sidecar outside the repo tree (%TEMP%), where the
        // BaseDirectory parent walk can never reach external/ — LAPLACE_ROOT wins when set.
        var root = Environment.GetEnvironmentVariable("LAPLACE_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            string rootRepo = Path.Combine(Path.GetFullPath(root), "external", "tree-sitter-grammars",
                                           $"tree-sitter-{modality}");
            string rp = sub is not null
                ? Path.Combine(rootRepo, sub, "queries", "tags.scm")
                : Path.Combine(rootRepo, "queries", "tags.scm");
            if (File.Exists(rp)) return rp;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string repoDir = Path.Combine(dir.FullName, "external", "tree-sitter-grammars",
                                          $"tree-sitter-{modality}");
            string p = sub is not null
                ? Path.Combine(repoDir, sub, "queries", "tags.scm")
                : Path.Combine(repoDir, "queries", "tags.scm");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
