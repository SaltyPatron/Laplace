using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;

internal static class NativeTestBootstrap
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [ModuleInitializer]
    internal static void Init()
    {
        if (!OperatingSystem.IsWindows()) return;

        var oneApi = new[]
        {
            @"C:\Program Files (x86)\Intel\oneAPI\mkl\latest\bin",
            @"C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin",
            @"C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin",
        };
        foreach (var dir in oneApi)
            if (Directory.Exists(dir))
                AddDllDirectory(dir);

        if (!TryResolveEngineBuildRoot(out var engineBuild))
            return;

        foreach (var (sub, name) in new (string, string)[]
                 {
                     ("core", "laplace_core"),
                     ("dynamics", "laplace_dynamics"),
                     ("synthesis", "laplace_synthesis"),
                 })
        {
            var path2 = Path.Combine(engineBuild, sub, name + ".dll");
            if (!File.Exists(path2)) continue;
            try { NativeLibrary.Load(path2); }
            catch (DllNotFoundException) { }
        }
    }

    private static bool TryResolveEngineBuildRoot(out string engineBuild)
    {
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_ENGINE_BUILD");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            engineBuild = Path.GetFullPath(fromEnv.Trim());
            if (Directory.Exists(engineBuild)) return true;
        }

        fromEnv = Environment.GetEnvironmentVariable("LAPLACE_BUILD_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            engineBuild = Path.GetFullPath(Path.Combine(fromEnv.Trim(), "build-win"));
            if (Directory.Exists(engineBuild)) return true;
        }

        if (LaplaceInstall.TryRepoRoot(out var root))
        {
            engineBuild = Path.Combine(root, "build-win");
            if (Directory.Exists(engineBuild)) return true;
        }

        engineBuild = "";
        return false;
    }
}
