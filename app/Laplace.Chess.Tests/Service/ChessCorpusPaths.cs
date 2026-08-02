namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Where the real chess corpora live, per host.
///
/// WHY THIS EXISTS. The corpus-backed tests hardcoded <c>D:\Data\Ingest\…</c>. Every one
/// of them is a <c>SkippableFact</c> gated on <c>File.Exists</c>, so on the Linux
/// self-hosted runner — the box CI actually runs on, where the corpora sit under
/// <c>/vault/Data</c> — they did not fail, they SKIPPED, and the suite reported green.
/// Three tests whose entire job is to prove the decomposers still read the real corpora
/// have therefore never executed in CI. A test that cannot run on the machine that runs
/// the tests is not a gate; it is a comment with a green checkmark.
///
/// <c>LAPLACE_DATA_ROOT</c> wins (that is what the ingest scripts honour), then the
/// platform default. Skipping stays available for a laptop with no corpus, but a
/// provisioned host now runs them.
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
