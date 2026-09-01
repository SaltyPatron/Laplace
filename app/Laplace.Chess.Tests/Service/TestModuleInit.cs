using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service.Tests;

internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        CodepointPerfcache.LoadDefault();

        // Fathom owns one process-global table set.  Several tests construct the real UCI
        // engine, which resolves the host's /vault table set through
        // ChessTablebaseRuntime.  The native fixture used to initialize the repository's
        // small tables later and then free them, so test order decided which set won and
        // could leave Fathom's global pointers referring to a different mapping.
        //
        // Give the entire test process one deterministic authority before any engine or
        // fixture can touch the runtime. This in-process test hook is not an environment
        // variable, so real UCI/gauntlet child processes still resolve the host's /vault set.
        // Production keeps ChessLabPaths' configured/data-root resolution unchanged.
        if (!LaplaceInstall.TryRepoRoot(out var root))
            throw new InvalidOperationException("repo root not resolvable for Syzygy fixture");

        SyzygyFixtureDirectory = Path.Combine(root, "test-data", "syzygy");
        if (!Directory.Exists(SyzygyFixtureDirectory))
            throw new DirectoryNotFoundException(
                $"Syzygy fixture directory missing: {SyzygyFixtureDirectory}");

        ChessTablebaseRuntime.ConfigureTestTableSet(SyzygyFixtureDirectory);
    }

    internal static string SyzygyFixtureDirectory { get; private set; } = "";
}
