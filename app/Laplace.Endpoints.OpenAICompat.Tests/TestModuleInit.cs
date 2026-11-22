using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat.Tests;

internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAPLACE_CHESS_OPENINGS"))
            && LaplaceInstall.TryRepoRoot(out var root))
        {
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", Path.Combine(root, "test-data", "chess-openings"));
        }
    }
}
