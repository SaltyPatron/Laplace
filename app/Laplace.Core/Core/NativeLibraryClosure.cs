using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Resolve the shipped Linux engine as one app-local dependency closure. The CLI can
/// enter Dynamics before Core; letting its DT_NEEDED use LD_LIBRARY_PATH first loads
/// a different core from the later app-local P/Invoke. Load dependencies explicitly
/// in order so the ELF loader reuses their SONAMEs, and return those same handles.
/// </summary>
internal static class NativeLibraryClosure
{
    private static readonly object Gate = new();
    private static IntPtr _core;
    private static IntPtr _dynamics;
    private static IntPtr _synthesis;
    private static IntPtr _syzygy;

#pragma warning disable CA2255 // Assembly-wide native imports require registration before the first import.
    [ModuleInitializer]
    internal static void Register()
    {
        if (OperatingSystem.IsLinux())
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryClosure).Assembly, Resolve);
    }
#pragma warning restore CA2255

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name is not ("laplace_core" or "laplace_dynamics" or "laplace_synthesis" or "laplace_syzygy"))
            return IntPtr.Zero;
        lock (Gate)
        {
            if (name == "laplace_syzygy") return Load(ref _syzygy, name);
            Load(ref _core, "laplace_core");
            if (name == "laplace_core") return _core;
            Load(ref _dynamics, "laplace_dynamics");
            if (name == "laplace_dynamics") return _dynamics;
            return Load(ref _synthesis, "laplace_synthesis");
        }
    }

    private static IntPtr Load(ref IntPtr handle, string name)
    {
        // P/Invoke caches function pointers for this process. Keep each successfully
        // loaded handle for the same lifetime; never unload it between requests.
        if (handle == IntPtr.Zero)
            handle = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, $"lib{name}.so"));
        return handle;
    }
}
