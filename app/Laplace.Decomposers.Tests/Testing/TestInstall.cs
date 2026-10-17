using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Tests;

internal static class TestInstall
{
    internal const int FullCiliMapMinRows = 100_000;

    internal static string ResolvePerfcacheOrThrow() => LaplaceInstall.ResolveT0Perfcache();

    internal static string ResolveCiliOrFallback()
    {
        try { return LaplaceInstall.ResolveCiliDir(); }
        catch (InvalidOperationException)
        {
            return OperatingSystem.IsWindows() ? @"D:\Data\Ingest\CILI" : "/vault/Data/CILI";
        }
    }

    internal static bool HasFullCiliMap(string? ciliDir = null)
    {
        string dir = ciliDir ?? ResolveCiliOrFallback();
        string path = Path.Combine(dir, IliMap.MapFileName);
        if (!File.Exists(path)) return false;
        try { return IliMap.Load(dir).Count >= FullCiliMapMinRows; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static string ResolveModelHubOrFallback()
    {
        try { return LaplaceInstall.ResolveModelHub(); }
        // An undeclared hub and a declared hub cache that does not exist on this host (a hosted
        // runner has no model estate) both fall back; tests then skip each absent snapshot.
        catch (Exception e) when (e is InvalidOperationException or DirectoryNotFoundException)
        {
            return OperatingSystem.IsWindows() ? @"D:\Models\hub" : "/vault/models/hub";
        }
    }
}
