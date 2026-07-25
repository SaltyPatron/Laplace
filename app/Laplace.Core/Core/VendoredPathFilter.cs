namespace Laplace.Decomposers.Code;

/// <summary>
/// Directory segments that mark vendored/third-party or build-artifact trees —
/// never the repo's own authored content, and never worth attesting under the
/// repo's identity. One shared list: CodeDecomposer and RepoDecomposer each
/// carried their own ad-hoc skip list before this (RepoDecomposer excluded
/// node_modules, CodeDecomposer didn't; neither excluded a Python venv), which
/// is how a 46MB pip-installed virtualenv (1,161 third-party .py files) got
/// ingested as if it were part of the "Hartonomous" repo (2026-07-23).
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

    // A single hand-authored source file this large is not a realistic thing
    // to expect from a human — it's a generated data dump, a vendored blob, or
    // a lockfile that happened to match a recognized extension. Decomposing it
    // token-by-token is exactly the "generated content should be ignored"
    // principle applied to size instead of location.
    private const long MaxFileBytes = 2 * 1024 * 1024;

    public static bool IsVendoredOrBuildPath(string file)
    {
        char sep = Path.DirectorySeparatorChar;
        foreach (var seg in Segments)
            if (file.Contains($"{sep}{seg}{sep}", StringComparison.Ordinal))
                return true;
        if (IsGeneratedFileName(file)) return true;
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
