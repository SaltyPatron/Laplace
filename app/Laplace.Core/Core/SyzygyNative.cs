using System.Text.RegularExpressions;

namespace Laplace.Engine.Core;

public static class SyzygyNative
{
    public const int Loss = 0;
    public const int BlessedLoss = 1;
    public const int Draw = 2;
    public const int CursedWin = 3;
    public const int Win = 4;

    private static readonly object InitGate = new();
    private static readonly Regex TableStemRegex = new(
        "^K[QRBNP]*vK[QRBNP]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string? _mappedPath;
    private static volatile int _mappedLargest;

    public static int Init(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        string normalized = NormalizeTablePath(path);
        if (normalized.Length == 0) return -1;

        lock (InitGate)
        {
            if (_mappedLargest > 0 && string.Equals(_mappedPath, normalized, StringComparison.Ordinal))
                return _mappedLargest;

            string[] dirs = normalized.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (dirs.Length == 0) return -1;

            var wdlStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dtzStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir))
                    return -1;

                CollectStemsAtDepth(dir, wdlStems, dtzStems);
                foreach (string subDir in Directory.EnumerateDirectories(dir))
                    CollectStemsAtDepth(subDir, wdlStems, dtzStems);
            }

            int largest = 0;
            foreach (string stem in wdlStems)
            {
                if (!dtzStems.Contains(stem) || !TableStemRegex.IsMatch(stem))
                    continue;

                int men = stem.Length - 1;
                if (men is >= 3 and <= 7 && men > largest)
                    largest = men;
            }

            _mappedLargest = largest;
            _mappedPath = largest > 0 ? normalized : null;
            return largest;
        }
    }

    private static void CollectStemsAtDepth(string dir, HashSet<string> wdlStems, HashSet<string> dtzStems)
    {
        foreach (string file in Directory.EnumerateFiles(dir, "*.rtbw", SearchOption.TopDirectoryOnly))
        {
            if (new FileInfo(file).Length > 0)
                wdlStems.Add(Path.GetFileNameWithoutExtension(file));
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.rtbz", SearchOption.TopDirectoryOnly))
        {
            if (new FileInfo(file).Length > 0)
                dtzStems.Add(Path.GetFileNameWithoutExtension(file));
        }
    }

    internal static string NormalizeTablePath(string path)
        => string.Join(Path.PathSeparator,
            path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

    public static void Free()
    {
        lock (InitGate)
        {
            _mappedLargest = 0;
            _mappedPath = null;
        }
    }

    public static int Largest() => _mappedLargest;

    public static int ProbeWdl(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove) => -1;

    public static (int Wdl, int Dtz)? ProbeRoot(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove) => null;

    public static (int Wdl, int Dtz, int From, int To, int Promotes)? ProbeRootTransition(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove) => null;
}
