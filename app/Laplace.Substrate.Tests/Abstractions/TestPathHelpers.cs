using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

internal static class TestPathHelpers
{
    internal static string CiliOrFallback()
    {
        try { return LaplaceInstall.ResolveCiliDir(); }
        catch (InvalidOperationException)
        {
            return OperatingSystem.IsWindows() ? @"D:\Data\Ingest\CILI" : "/vault/Data/CILI";
        }
    }

    internal static string Iso639OrFallback()
    {
        try { return LaplaceInstall.ResolveIso639Dir(); }
        catch (InvalidOperationException)
        {
            return OperatingSystem.IsWindows() ? @"D:\Data\Ingest\ISO639" : "/vault/Data/ISO639";
        }
    }
}
