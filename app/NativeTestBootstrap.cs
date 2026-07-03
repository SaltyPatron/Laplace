using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;




internal static class NativeTestBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (!OperatingSystem.IsWindows()) return;

        // laplace_synthesis (and dynamics under MKL) resolve oneAPI runtime
        // DLLs from PATH; test hosts launched without env.cmd don't have
        // them. Prepend the oneAPI bins so NativeLibrary.Load can bind
        // dependencies exactly like the script-driven processes do.
        var oneApi = new[]
        {
            @"C:\Program Files (x86)\Intel\oneAPI\mkl\latest\bin",
            @"C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin",
            @"C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin",
        };
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in oneApi)
            if (Directory.Exists(dir) && !path.Contains(dir, StringComparison.OrdinalIgnoreCase))
                path = dir + ";" + path;
        Environment.SetEnvironmentVariable("PATH", path);

        string? root = Environment.GetEnvironmentVariable("LAPLACE_ROOT");

        // Build outputs live outside the repo (Directory.Build.props), so an
        // ancestor walk from the test binary can't find build-win — prefer
        // the repo root stamped into every assembly at build time.
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
