using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

internal static class NativeTestBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        LaplaceCoreGate.InitializeNativeResolver();
    }
}
