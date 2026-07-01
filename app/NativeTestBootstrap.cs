using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;





internal static class NativeTestBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (!OperatingSystem.IsWindows()) return;

        string? root = Environment.GetEnvironmentVariable("LAPLACE_ROOT");
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
            var path = Path.Combine(root, "build-win", sub, name + ".dll");
            if (!File.Exists(path)) continue;
            try { NativeLibrary.Load(path); }
            catch (DllNotFoundException) { }
        }
    }
}
