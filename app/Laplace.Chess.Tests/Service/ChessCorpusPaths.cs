namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Where the admitted chess corpora live on this host: <c>LAPLACE_DATA_ROOT</c> (the root
/// ingest honours) if set, else the platform default (<c>/vault/Data</c> on Linux).
/// Corpus-backed tests skip only when the corpus is absent from that root.
/// </summary>
internal static class ChessCorpusPaths
{
    internal static string DataRoot =>
        Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT") is { Length: > 0 } root
            ? root
            : OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data";

    internal static string Books => Path.Combine(DataRoot, "test-data", "text");

    internal static string Openings => Path.Combine(DataRoot, "Games", "Chess", "openings");

    internal static string Games => Path.Combine(DataRoot, "Games", "Chess");
}
