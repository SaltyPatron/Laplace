namespace Laplace.Engine.Core;

/// <summary>
/// Directory segments that mark vendored/third-party or build-artifact trees —
/// never the repo's own authored content, and never worth attesting under the
/// repo's identity. One shared list: CodeDecomposer and RepoDecomposer each
/// carried their own ad-hoc skip list before this (RepoDecomposer excluded
/// node_modules, CodeDecomposer didn't; neither excluded a Python venv), which
/// is how a 46MB pip-installed virtualenv (1,161 third-party .py files) got
/// ingested as if it were part of the "Hartonomous" repo (2026-07-23).
///
/// Lives in Laplace.Core (was Laplace.Decomposers.Code) so the Substrate-side
/// DocumentDecomposer can share the SAME filter — Substrate cannot reference
/// Decomposers (wrong dependency direction), and a third copy is exactly the
/// divergence this class exists to prevent (GH #608, 2026-07-24).
///
/// "external"/"ext" joined the list the same day after a 20MB single-file
/// vendored data dump (wiktextract's taxondata.py, pulled in via a vendored
/// PostGIS+Eigen+wiktextract tree under Engine/external/) pinned ~43GB RSS and
/// produced zero ingest progress for 14+ minutes before being killed — a file
/// size cap below catches this class of problem even when the directory name
/// doesn't match anything on this list.
/// </summary>
public static class VendoredPathFilter
{
    private static readonly string[] Segments =
    [
        "obj", "bin", ".git", "node_modules",
        ".venv", "venv", "__pycache__", "site-packages", ".tox",
        ".mypy_cache", ".pytest_cache",
        "dist", "build", "target", ".next", "vendor",
        "external", "ext", "extern", "third_party", "3rdparty", "thirdparty",
    ];

    // A single hand-authored SOURCE file this large is not a realistic thing
    // to expect from a human — it's a generated data dump, a vendored blob, or
    // a lockfile that happened to match a recognized extension. Decomposing it
    // token-by-token is exactly the "generated content should be ignored"
    // principle applied to size instead of location.
    //
    // SCOPE (GH #754, 2026-08-10): this is a heuristic about SOURCE CODE
    // provenance and it does not transfer to a corpus. On a document or media
    // lane the premise inverts — large files are books, recordings and images,
    // which are the corpus rather than an accident in it. Applied to
    // test-data/text it silently discarded webster-unabridged-dictionary-1913
    // (27.6 MB) and britannica-1911-bulgaria-to-calgary (2.02 MB): the two
    // densest lexical documents in the set, dropped at ENUMERATION, so
    // files_total reported 207 against 209 on disk and no journal row recorded
    // the loss. Use IsVendoredOrBuildPath for code lanes; use
    // IsVendoredOrBuildLocation for corpus lanes, which asks only the
    // provenance question.
    private const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Provenance only: is this path inside a vendored/build tree, or is its
    /// filename conventionally tool-generated? No size heuristic. This is the
    /// predicate corpus lanes want — a large book is not a build artifact.
    /// </summary>
    public static bool IsVendoredOrBuildLocation(string file)
    {
        char sep = Path.DirectorySeparatorChar;
        foreach (var seg in Segments)
            if (file.Contains($"{sep}{seg}{sep}", StringComparison.Ordinal))
                return true;
        return IsGeneratedFileName(file);
    }

    /// <summary>
    /// Provenance plus the source-code size heuristic. Code lanes only — see the
    /// MaxFileBytes scope note.
    /// </summary>
    public static bool IsVendoredOrBuildPath(string file)
    {
        if (IsVendoredOrBuildLocation(file)) return true;
        try { return new FileInfo(file).Length > MaxFileBytes; }
        catch (IOException) { return false; }
    }

    // Tool-emitted files that use a normal, recognized extension — so the
    // directory-segment check above can't catch them — but are conventionally
    // marked as generated, not hand-authored: EF/WinForms/protobuf/resx
    // designer output, T4/codegen output. Filename-only (no content read):
    // the caller checks this before reading the file at all.
    private static readonly string[] GeneratedSuffixes =
    [
        ".designer.cs", ".g.cs", ".g.i.cs", ".pb.cs", ".generated.cs",
        ".designer.vb", ".g.vb",
    ];

    private static bool IsGeneratedFileName(string file)
    {
        foreach (var suffix in GeneratedSuffixes)
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
