using System.IO;
using System.IO.Compression;
using System.Text;

namespace Laplace.Chess.Service;

/// <summary>
/// Input resolution shared by every chess lane (PGN / openings / books).
///
/// WHY THIS EXISTS. Each lane used to do its own
/// <c>Directory.EnumerateFiles(path, "*.pgn", scope)</c> and yield nothing when that
/// matched nothing. Nothing propagated: <c>DescribeInputAsync</c> returned null, the
/// runner started with no inventory, zero records composed, and the process exited 0
/// with "done: 0 intents applied". Measured on this box:
///
///   laplace ingest chess /vault/Data/Games/Chess/Lumbras   ->  EXIT=0, 0 entities
///
/// pointed at 18 GB of chess games. The games are one level down (<c>Lumbras/otb</c>,
/// <c>Lumbras/online</c>) or still inside the <c>.7z</c> archives beside them, and the
/// non-recursive default is deliberate — but a run that reads nothing and reports
/// success is a green that means nothing. The CI seed workflows call the same path, so
/// a mis-typed corpus directory produced a green seed job with an empty substrate.
///
/// Empty input is a FAILURE here, and the exception says what was searched, what is
/// actually there, and the exact flag or command that fixes it.
/// </summary>
internal static class ChessInput
{
    /// <summary>Extensions the PGN lane reads. <c>.zip</c>/<c>.gz</c> are decompressed inline.</summary>
    internal static readonly string[] PgnExtensions = [".pgn", ".pgn.gz", ".gz", ".zip"];

    internal static readonly string[] OpeningsExtensions = [".tsv"];

    internal static readonly string[] BookExtensions = [".txt"];

    /// <summary>
    /// Archive formats a chess corpus ships in that the BCL cannot open in-process.
    /// Skipping these silently is what made <c>Lumbras/</c> (twenty-one <c>.7z</c>) a
    /// zero-row success; they are named in the error with the command that extracts them.
    /// </summary>
    private static readonly (string Ext, string Extract)[] UnsupportedArchives =
    [
        (".7z",  "7z x '{0}' -o'{1}'"),
        (".rar", "unrar x '{0}' '{1}'"),
        (".zst", "zstd -d '{0}'"),
        (".bz2", "bunzip2 -k '{0}'"),
        (".xz",  "unxz -k '{0}'"),
    ];

    /// <summary>
    /// The files a lane will actually read, or a thrown <see cref="ChessInputException"/>
    /// explaining why there are none. Never returns an empty list.
    ///
    /// An explicit FILE path is honoured whatever its extension — naming one file is an
    /// operator decision and the parser is the real validity gate. A DIRECTORY is filtered
    /// by <paramref name="extensions"/>.
    /// </summary>
    internal static IReadOnlyList<string> Resolve(
        string path, SearchOption scope, IReadOnlyList<string> extensions, string lane)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ChessInputException(
                $"{lane}: no corpus path given. Usage: laplace ingest {lane} <file-or-directory>");

        if (File.Exists(path)) return [Path.GetFullPath(path)];

        if (!Directory.Exists(path))
            throw new ChessInputException(
                $"{lane}: '{path}' does not exist (neither a file nor a directory on this host).");

        var hits = Match(path, scope, extensions);
        if (hits.Count > 0) return hits;

        throw new ChessInputException(Diagnose(path, scope, extensions, lane));
    }

    private static List<string> Match(string dir, SearchOption scope, IReadOnlyList<string> extensions)
    {
        var hits = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", scope))
            if (HasExtension(f, extensions))
                hits.Add(f);
        hits.Sort(StringComparer.Ordinal);
        return hits;
    }

    // Two-part extensions (".pgn.gz") must match before the single-part fallback, and the
    // comparison is ordinal-ignore-case because a corpus copied off Windows carries ".PGN".
    internal static bool HasExtension(string path, IReadOnlyList<string> extensions)
    {
        string name = Path.GetFileName(path);
        foreach (var ext in extensions)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// The message a zero-match directory earns: what was searched, whether the corpus is
    /// one level down, whether it is sitting in archives, and otherwise what extensions the
    /// directory does hold. Every branch names the fix.
    /// </summary>
    private static string Diagnose(
        string dir, SearchOption scope, IReadOnlyList<string> extensions, string lane)
    {
        var sb = new StringBuilder();
        sb.Append(lane).Append(": no input files under '").Append(dir).Append("' matching ")
          .Append(string.Join(", ", extensions))
          .Append(scope == SearchOption.TopDirectoryOnly ? " (top directory only)." : " (recursive).");

        if (scope == SearchOption.TopDirectoryOnly)
        {
            var deep = Match(dir, SearchOption.AllDirectories, extensions);
            if (deep.Count > 0)
            {
                sb.Append("\n  ").Append(deep.Count)
                  .Append(" matching file(s) DO exist in subdirectories, e.g. ")
                  .Append(string.Join(", ", deep.Take(3).Select(Path.GetDirectoryName).Distinct()))
                  .Append(".\n  Re-run with --recursive, or point at the subdirectory directly.");
                return sb.ToString();
            }
        }

        var archives = new List<string>();
        foreach (var (ext, extract) in UnsupportedArchives)
        {
            var found = Directory.EnumerateFiles(dir, "*" + ext, scope).OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (found.Count == 0) continue;
            archives.Add($"{found.Count}x {ext} (extract: "
                         + string.Format(extract, found[0], dir) + ")");
        }
        if (archives.Count > 0)
        {
            sb.Append("\n  The directory holds archives this lane cannot open in-process: ")
              .Append(string.Join("; ", archives))
              .Append(".\n  Extract them next to the archives and re-run.");
            return sb.ToString();
        }

        var present = Directory.EnumerateFiles(dir, "*", scope)
            .Select(f => Path.GetExtension(f).ToLowerInvariant())
            .Where(e => e.Length > 0)
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .Take(6)
            .Select(g => $"{g.Count()}x {g.Key}")
            .ToList();
        sb.Append(present.Count > 0
            ? "\n  Extensions present instead: " + string.Join(", ", present) + "."
            : "\n  The directory is empty.");
        return sb.ToString();
    }

    /// <summary>
    /// Every readable text member of one input path: the file itself, the gunzipped
    /// stream for <c>.gz</c>, or one reader per entry for <c>.zip</c>. TWIC ships each
    /// weekly issue as <c>twicNNNN.zip</c> holding one <c>.pgn</c>; that used to be
    /// invisible to a <c>*.pgn</c> glob even though the BCL opens it in-process.
    /// </summary>
    internal static IEnumerable<(string Name, TextReader Reader)> OpenMembers(string path)
    {
        string name = Path.GetFileName(path);

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                if (entry.Length == 0) continue;                    // directory marker
                if (entry.FullName.EndsWith('/')) continue;
                using var s = entry.Open();
                using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                yield return ($"{name}!{entry.FullName}", r);
            }
            yield break;
        }

        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new StreamReader(gz, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            yield return (name, r);
            yield break;
        }

        using var plain = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        yield return (name, plain);
    }

    /// <summary>
    /// Uncompressed byte length, used by the inventory estimators. A compressed member's
    /// real size is what the progress denominator needs; <c>FileInfo.Length</c> on a
    /// <c>.zip</c> is the compressed size and under-counts the corpus by ~4x.
    /// </summary>
    internal static long UncompressedLength(string path)
    {
        string name = Path.GetFileName(path);
        try
        {
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                return zip.Entries.Sum(e => e.Length);
            }
        }
        catch (InvalidDataException) { /* corrupt archive — fall through to file size */ }
        return new FileInfo(path).Length;
    }

    /// <summary>True when the path needs decompression to read (no cheap byte-scan).</summary>
    internal static bool IsCompressed(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Thrown when a chess lane has nothing to read. Distinct type so the runner and the tests
/// can tell "operator pointed at the wrong place" apart from a parse failure mid-corpus.
/// </summary>
public sealed class ChessInputException(string message) : InvalidOperationException(message);
