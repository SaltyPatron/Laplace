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

        string? root = LaplaceInstall.TryRepoRoot(out var repo) ? repo : null;

        if (string.IsNullOrEmpty(root))
        {
            root = typeof(NativeTestBootstrap).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "LaplaceRepoRoot")?.Value;
            if (root is not null && !Directory.Exists(Path.Combine(root, "build-win", "core")))
                root = null;
        }
        if (string.IsNullOrEmpty(root))
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir is not null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "build-win", "core")))
                {
                    root = dir;
                    break;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        if (root is null) return;

        foreach (var (sub, name) in new (string, string)[]
                 {
                     ("core", "laplace_core"),
                     ("dynamics", "laplace_dynamics"),
                     ("synthesis", "laplace_synthesis"),
                 })
        {
            var path2 = Path.Combine(root, "build-win", sub, name + ".dll");
            if (!File.Exists(path2)) continue;
            try { NativeLibrary.Load(path2); }
            catch (DllNotFoundException) { }
        }
    }
}
