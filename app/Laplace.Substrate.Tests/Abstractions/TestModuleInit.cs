using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void Init() => CodepointPerfcache.LoadDefault();
}
