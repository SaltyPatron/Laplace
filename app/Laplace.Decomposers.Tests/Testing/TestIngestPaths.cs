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
}
