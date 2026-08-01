using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// One entry in an <see cref="IngestSourceLayout"/>'s file list: either an exact file
/// name or a glob. Order is meaningful — callers that take the first hit (SemLink's
/// PredicateMatrix, Wiktionary) depend on the canonical name preceding the glob that
/// would also match it.
/// </summary>
/// <param name="Value">File name, or glob pattern when <paramref name="IsGlob"/>.</param>
/// <param name="SkipFileName">Optional exclusion applied to the file NAME of glob hits.</param>
public readonly record struct IngestFileMatch(
    string Value,
    bool IsGlob,
    Func<string, bool>? SkipFileName = null)
{
    public static IngestFileMatch Name(string fileName) => new(fileName, false);

    public static IngestFileMatch Glob(string pattern, Func<string, bool>? skipFileName = null) =>
        new(pattern, true, skipFileName);
}

/// <summary>
/// Where a source's files live, declared instead of hand-walked. Eleven decomposers each
/// wrote their own `DataDirs`/`VaultRoots`/`seen`-set triad, three of them byte-identical,
/// and every one re-hardcoded the platform data root one line after calling
/// <see cref="LaplaceInstall.ResolveIngestRoot"/>. A layout names the directories and the
/// file entries; <see cref="IngestInput.Locate"/> walks them in declaration order and
/// dedups.
/// </summary>
public sealed record IngestSourceLayout
{
    /// <summary>File names/globs looked for in every candidate directory, in order.</summary>
    public IReadOnlyList<IngestFileMatch> Files { get; init; } = [];

    /// <summary>Directories relative to the ecosystem path, in probe order. "." is the
    /// ecosystem path itself — listed explicitly because some sources (SemLink's role
    /// mapping) probe their subdirectories BEFORE the root.</summary>
    public IReadOnlyList<string> EcosystemDirs { get; init; } = ["."];

    /// <summary>Subdirectories probed under each ingest root, after the root itself and
    /// after <see cref="RootDirectoryGlobs"/>.</summary>
    public IReadOnlyList<string> RootDirs { get; init; } = [];

    /// <summary>Globs matched against the immediate subdirectories of each ingest root
    /// (e.g. "MapNet*" to catch a versioned unpack directory).</summary>
    public IReadOnlyList<string> RootDirectoryGlobs { get; init; } = [];

    /// <summary>Subdirectories of EVERY candidate directory that are searched for the same
    /// <see cref="Files"/>, immediately after the directory itself.</summary>
    public IReadOnlyList<string> NestedDirs { get; init; } = [];

    /// <summary>Probe <see cref="LaplaceInstall.ResolveIngestRoot"/> as well as the
    /// ecosystem path. Off by default: a source whose data is only ever under the path it
    /// was handed must not go fishing in the shared data root.</summary>
    public bool SearchIngestRoots { get; init; }

    /// <summary>Also treat the ecosystem path's parent directory as an ingest root — an
    /// unpack sibling ("…/Ingest/SemLink" alongside "…/Ingest/PredicateMatrix").</summary>
    public bool IncludeEcosystemParent { get; init; }

    /// <summary>Recursion for glob entries. Exact-name entries are always looked up
    /// directly in the candidate directory.</summary>
    public SearchOption Search { get; init; } = SearchOption.TopDirectoryOnly;
}

/// <summary>
/// Shared input resolution for multi-file decomposers (the "valets"): a source root
/// passed on the CLI may be a single file, a directory of matching files, or an
/// ecosystem root containing a known subdirectory. Decomposers stay thin — they name
/// their glob pattern and optional ecosystem subdir; this resolves the file list so
/// every multi-file source (UD, Tatoeba, OMW, …) supports `ingest &lt;source&gt; &lt;path&gt;`
/// down to a single file, without per-decomposer file-walking logic.
/// </summary>
public static class IngestInput
{
    /// <param name="root">CLI path or default ecosystem path.</param>
    /// <param name="pattern">Glob for matching files, e.g. "*.conllu".</param>
    /// <param name="ecosystemSubdir">If <paramref name="root"/> is the ecosystem root,
    /// the subdirectory the corpus actually lives under (e.g. "ud-treebanks-v2.17").</param>
    public static List<string> ResolveFiles(string root, string pattern, string? ecosystemSubdir = null)
    {
        // Explicit single file: ingest exactly that (used to re-run/validate one file).
        if (File.Exists(root))
            return [root];

        string dir = root;
        if (ecosystemSubdir is not null)
        {
            string sub = Path.Combine(root, ecosystemSubdir);
            if (Directory.Exists(sub))
                dir = sub;
        }
        if (!Directory.Exists(dir))
            return [];
        // SORTED. Directory.EnumerateFiles guarantees NO ordering -- on Linux it returns
        // filesystem order, which is directory-hash order and shifts as entries are added,
        // removed, or inodes reused. That makes the order of a multi-file source an input
        // the code leaves to the filesystem.
        //
        // MEASURED: two foundation seeds over identical inputs produced semlink row counts
        // differing by +12 and then +7. Ids are content hashes, so identical content must
        // yield identical rows; a varying count means something order-dependent reached the
        // write path (batch boundaries move, and the working-set stage dedup absorbs a
        // repeat only WITHIN a batch). Sorting removes the variable at its source and costs
        // one comparison sort per source resolution.
        return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
                        .OrderBy(static p => p, StringComparer.Ordinal)
                        .ToList();
    }

    /// <summary>True when the resolved root is a single explicit file — callers can
    /// then skip corpus-wide filters (e.g. UD's language filter) the operator overrode.</summary>
    public static bool IsSingleFile(string root) => File.Exists(root);

    /// <summary>First subdirectory of <paramref name="root"/> that actually holds a
    /// matching file, else <paramref name="root"/>. The degenerate "one subdir, one
    /// pattern" case of <see cref="Locate"/>, kept because the XML sources want the
    /// DIRECTORY, not its file list.</summary>
    public static string ResolveSubdir(string root, string pattern, params string[] subdirs)
    {
        foreach (var sub in subdirs)
        {
            var dir = Path.Combine(root, sub);
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any())
                return dir;
        }
        return root;
    }

    /// <summary>
    /// Every file the layout finds under <paramref name="ecosystemPath"/> and the ingest
    /// roots, in declaration order, deduplicated. Lazy: callers that only need the first
    /// hit (or `.Any()`) stop the walk there.
    /// </summary>
    public static IEnumerable<string> Locate(string ecosystemPath, IngestSourceLayout layout)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in CandidateDirectories(ecosystemPath, layout))
        {
            foreach (string path in MatchIn(dir, layout))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    /// <summary>
    /// The layout's files inside one already-known directory (and its
    /// <see cref="IngestSourceLayout.NestedDirs"/>) — the "is this source unpacked HERE"
    /// question, without the ingest-root search.
    /// </summary>
    public static IEnumerable<string> FilesIn(string dir, IngestSourceLayout layout)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in WithNested(dir, layout))
        {
            foreach (string path in MatchIn(candidate, layout))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    /// <summary>Directories the layout will search, in probe order.</summary>
    public static IEnumerable<string> CandidateDirectories(string ecosystemPath, IngestSourceLayout layout)
    {
        // Ordinal, NOT OrdinalIgnoreCase: layouts list case variants of the same unpack
        // name ("MapNet" and "mapnet") because on Linux those are two directories. Folding
        // them here would silently drop one. File-path dedup stays case-insensitive, which
        // is what the hand-rolled copies did.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string rel in layout.EcosystemDirs)
        {
            foreach (string dir in WithNested(Combine(ecosystemPath, rel), layout))
            {
                if (seen.Add(dir)) yield return dir;
            }
        }

        foreach (string root in IngestRoots(ecosystemPath, layout))
        {
            foreach (string dir in WithNested(root, layout))
            {
                if (seen.Add(dir)) yield return dir;
            }

            // Directory.EnumerateDirectories throws on a missing root — the hand-rolled
            // copies called it unguarded, so a box without the shared data root faulted
            // the whole enumeration instead of finding nothing.
            foreach (string glob in Directory.Exists(root) ? layout.RootDirectoryGlobs : [])
            {
                // Sorted for the same reason as EnumerateFiles above: the glob's match order
                // is filesystem order, so an unpacked-directory layout could change which
                // candidate is visited first between runs over identical inputs.
                foreach (string matched in Directory.EnumerateDirectories(root, glob)
                                                    .OrderBy(static d => d, StringComparer.Ordinal))
                {
                    foreach (string dir in WithNested(matched, layout))
                    {
                        if (seen.Add(dir)) yield return dir;
                    }
                }
            }

            foreach (string rel in layout.RootDirs)
            {
                foreach (string dir in WithNested(Combine(root, rel), layout))
                {
                    if (seen.Add(dir)) yield return dir;
                }
            }
        }
    }

    private static IEnumerable<string> WithNested(string dir, IngestSourceLayout layout)
    {
        yield return dir;
        foreach (string nested in layout.NestedDirs)
            yield return Path.Combine(dir, nested);
    }

    private static string Combine(string root, string relative) =>
        relative is "." or "" ? root : Path.Combine(root, relative);

    private static IEnumerable<string> MatchIn(string dir, IngestSourceLayout layout)
    {
        if (!Directory.Exists(dir)) yield break;

        foreach (var entry in layout.Files)
        {
            if (!entry.IsGlob)
            {
                string exact = Path.Combine(dir, entry.Value);
                if (File.Exists(exact)) yield return exact;
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, entry.Value, layout.Search))
            {
                if (entry.SkipFileName?.Invoke(Path.GetFileName(file)) == true) continue;
                yield return file;
            }
        }
    }

    /// <summary>
    /// The shared data roots, resolved ONCE through <see cref="LaplaceInstall.ResolveIngestRoot"/>.
    /// It throws when no data root is installed (a CI box, a dev checkout without the
    /// vault); a source that simply has no files there is not an error, so the throw
    /// becomes "no candidates" rather than a fault mid-enumeration.
    /// </summary>
    private static IEnumerable<string> IngestRoots(string ecosystemPath, IngestSourceLayout layout)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (layout.SearchIngestRoots)
        {
            string? ingest = TryIngestRoot();
            if (ingest is not null && seen.Add(ingest))
                yield return ingest;
        }

        if (layout.IncludeEcosystemParent)
        {
            string? parent = null;
            try { parent = Path.GetDirectoryName(Path.GetFullPath(ecosystemPath)); }
            catch (ArgumentException) { }
            if (!string.IsNullOrEmpty(parent) && seen.Add(parent))
                yield return parent;
        }
    }

    private static string? TryIngestRoot()
    {
        try { return LaplaceInstall.ResolveIngestRoot(); }
        catch (InvalidOperationException) { return null; }
    }
}
