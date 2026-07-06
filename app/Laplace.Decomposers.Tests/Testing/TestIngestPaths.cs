namespace Laplace.Decomposers.Tests;

internal static class TestIngestPaths
{
    public static string Root =>
        Environment.GetEnvironmentVariable("LAPLACE_INGEST_ROOT") is { Length: > 0 } r
            ? r
            : OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data";

    public static string UcdLatest => Path.Combine(Root, "UCD", "Public", "UCD", "latest");

    public static string Iso639 => Path.Combine(Root, "ISO639");

    public static string OpenSubtitles => Path.Combine(Root, "OpenSubtitles");
}
