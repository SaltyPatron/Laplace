namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Where the admitted chess corpora live on this host: <c>LAPLACE_DATA</c> (the root
/// ingest honours), declared in the environment or machine.env.
/// Corpus-backed tests skip only when the corpus is absent from that root.
/// </summary>
internal static class ChessCorpusPaths
{
    internal static string DataRoot =>
        Laplace.Engine.Core.LaplaceInstall.ResolveIngestRoot();

    internal static string Books => Path.Combine(DataRoot, "test-data", "text");

    internal static string Openings => Path.Combine(DataRoot, "Games", "Chess", "openings");

    internal static string Games => Path.Combine(DataRoot, "Games", "Chess");
}
