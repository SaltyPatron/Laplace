namespace Laplace.Engine.Core;

/// <summary>
/// Artifact-selection predicate shared by every provider that enumerates a source tree:
/// directory segments and generated-file suffixes that mark vendored/third-party or
/// build-artifact content, which is not testimony of the selected source and is not
/// admitted under that source's identity.
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

    // A hand-authored source-code file above this size is taken as generated or
    // vendored content (a data dump, blob, or lockfile with a recognized extension).
    // The heuristic is about source-code provenance only: for text, image, and audio
    // artifacts a large file is the content itself, so those selections use
    // IsVendoredOrBuildLocation, which asks only the provenance question.
    private const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Provenance only: is this path inside a vendored/build tree, or is its
    /// filename conventionally tool-generated? No size heuristic: a large document
    /// or recording is content, not a build artifact.
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
    /// Evaluate the path relative to the selected source root, so the directory hosting
    /// the source does not match a segment; a leading separator keeps first-segment matching.
    /// </summary>
    public static bool IsVendoredOrBuildLocation(string file, string sourceRoot) =>
        IsVendoredOrBuildLocation(
            Path.DirectorySeparatorChar + Path.GetRelativePath(sourceRoot, file));

    /// <summary>
    /// Provenance plus the source-code size heuristic (see MaxFileBytes); applies when
    /// selecting source-code artifacts.
    /// </summary>
    public static bool IsVendoredOrBuildPath(string file)
    {
        if (IsVendoredOrBuildLocation(file)) return true;
        return IsOversizedSource(file);
    }

    public static bool IsVendoredOrBuildPath(string file, string sourceRoot)
    {
        if (IsVendoredOrBuildLocation(file, sourceRoot)) return true;
        return IsOversizedSource(file);
    }

    private static bool IsOversizedSource(string file)
    {
        try { return new FileInfo(file).Length > MaxFileBytes; }
        catch (IOException) { return false; }
    }

    // Tool-emitted files that use a normal, recognized extension — so the
    // directory-segment check above can't catch them — but are conventionally
    // marked as generated, not hand-authored: EF/WinForms/protobuf/resx
    // designer output, T4/codegen output. Filename-only, so it runs before any read.
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
