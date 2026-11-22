using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Resolves shipped Laplace-Native (<c>laplace</c>) and Laplace-Engine (<c>laplace_engine_abi</c>)
/// shared libraries from app-local runtime output on Linux so a single coherent closure loads.
/// </summary>
internal static class NativeLibraryClosure
{
    private static readonly object Gate = new();
    private static IntPtr _native;
    private static IntPtr _engineAbi;

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Register()
    {
        if (OperatingSystem.IsLinux())
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryClosure).Assembly, Resolve);
    }
#pragma warning restore CA2255

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name is not ("laplace" or "laplace_engine_abi"))
            return IntPtr.Zero;
        lock (Gate)
        {
            Load(ref _native, "laplace");
            if (name == "laplace") return _native;
            return Load(ref _engineAbi, "laplace_engine_abi");
        }
    }

    private static IntPtr Load(ref IntPtr handle, string name)
    {
        if (handle == IntPtr.Zero)
        {
            var local = Path.Combine(AppContext.BaseDirectory, $"lib{name}.so");
            if (File.Exists(local))
                handle = NativeLibrary.Load(local);
        }
        return handle;
    }
}
