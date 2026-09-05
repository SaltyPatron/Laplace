using Laplace.Engine.Core;

namespace Laplace.Decomposers.Tests;

internal static class TestIngestPaths
{
    public static string Root
    {
        get
        {
            try { return LaplaceInstall.ResolveIngestRoot(); }
            catch (InvalidOperationException)
            {
                return OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data";
            }
        }
    }

    public static string UcdLatest => Path.Combine(Root, "UCD", "Public", "UCD", "latest");

    public static string Iso639 => Path.Combine(Root, "ISO639");

    public static string OpenSubtitles => Path.Combine(Root, "OpenSubtitles");

    public static string Receipt(string fileName)
    {
        string? configured = Environment.GetEnvironmentVariable("LAPLACE_TEST_RECEIPT_DIR");
        string directory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            directory = Path.GetFullPath(configured.Trim());
        }
        else
        {
            if (!LaplaceInstall.TryRepoRoot(out string repoRoot))
                throw new InvalidOperationException("Repository root not found for test receipt");
            directory = Path.Combine(repoRoot, "build", "test-receipts");
        }

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }
}
