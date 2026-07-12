using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Laplace.Decomposers.Abstractions;

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
        return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).ToList();
    }

    /// <summary>True when the resolved root is a single explicit file — callers can
    /// then skip corpus-wide filters (e.g. UD's language filter) the operator overrode.</summary>
    public static bool IsSingleFile(string root) => File.Exists(root);
}
