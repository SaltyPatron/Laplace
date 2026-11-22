using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Laplace.Native;

namespace Laplace.Engine.Core;

public static class LaplaceCoreGate
{
    public static readonly object Native = new();

    private static int _initialized;
    private static readonly Lock ResolverLock = new();
    private static IntPtr _laplaceNativeHandle;

#pragma warning disable CA2255
    [ModuleInitializer]
    public static void InitializeNativeResolver()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        ConfigureSearchPath();
        EnsureDefaultTier0Setting();
        NativeLibrary.SetDllImportResolver(typeof(LaplaceNative).Assembly, ResolveLaplaceLibrary);
    }
#pragma warning restore CA2255

    private static void ConfigureSearchPath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            dirs.Add(AppContext.BaseDirectory);

        foreach (var dir in new[]
        {
            @"C:\Program Files (x86)\Intel\oneAPI\mkl\latest\bin",
            @"C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin",
            @"C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin",
        })
        {
            if (Directory.Exists(dir))
                dirs.Add(dir);
        }

        var pgDir = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_PG_DIR");
        if (string.IsNullOrWhiteSpace(pgDir) && Directory.Exists(@"D:\Libraries\postgresql\18.6"))
            pgDir = @"D:\Libraries\postgresql\18.6";
        if (!string.IsNullOrWhiteSpace(pgDir))
        {
            var pgBin = Path.Combine(pgDir, "bin");
            if (Directory.Exists(pgBin))
                dirs.Add(pgBin);
        }

        var depsDir = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_DEPS");
        if (string.IsNullOrWhiteSpace(depsDir) && Directory.Exists(@"D:\Libraries\deps"))
            depsDir = @"D:\Libraries\deps";
        if (!string.IsNullOrWhiteSpace(depsDir) && Directory.Exists(depsDir))
        {
            foreach (var dep in new[] { "icu", "zlib", "zstd", "libxml2" })
            {
                var depRoot = Path.Combine(depsDir, dep);
                if (!Directory.Exists(depRoot))
                    continue;
                AddBinDirs(depRoot, dirs);
                foreach (var verDir in Directory.GetDirectories(depRoot))
                    AddBinDirs(verDir, dirs);
            }
        }

        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extra = dirs
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => existingPath.IndexOf(d, StringComparison.OrdinalIgnoreCase) < 0)
            .ToArray();
        if (extra.Length > 0)
        {
            var joined = string.Join(Path.PathSeparator, extra);
            Environment.SetEnvironmentVariable("PATH",
                string.IsNullOrEmpty(existingPath) ? joined : joined + Path.PathSeparator + existingPath);
        }
    }

    private static void AddBinDirs(string root, List<string> dirs)
    {
        var bin64 = Path.Combine(root, "bin64");
        if (Directory.Exists(bin64)) dirs.Add(bin64);
        var bin = Path.Combine(root, "bin");
        if (Directory.Exists(bin)) dirs.Add(bin);
    }

    private static void EnsureDefaultTier0Setting()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAPLACE_TIER0")))
            return;

        var candidates = new List<string>();
        var configured = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_TIER0");
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured);
        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "share", "laplace", "tier0.bin"));

        var envPkg = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(envPkg))
            candidates.Add(Path.Combine(envPkg, "share", "laplace", "tier0.bin"));

        var work = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK");
        if (!string.IsNullOrWhiteSpace(work))
            candidates.Add(Path.Combine(work, "tier0", "tier0.bin"));

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(@"E:\Data\Laplace\work\tier0\tier0.bin");
            candidates.Add(@"E:\Data\Laplace\deploy\share\tier0.bin");
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                Environment.SetEnvironmentVariable("LAPLACE_TIER0", Path.GetFullPath(path));
                return;
            }
        }
    }

    private static IntPtr ResolveLaplaceLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "laplace", StringComparison.Ordinal))
            return IntPtr.Zero;

        lock (ResolverLock)
        {
            if (_laplaceNativeHandle != IntPtr.Zero)
                return _laplaceNativeHandle;

            var abiName = OperatingSystem.IsWindows() ? "laplace_engine_abi.dll" : "liblaplace_engine_abi.so";
            var nativeName = OperatingSystem.IsWindows() ? "laplace.dll" : "liblaplace.so";

            var candidates = new List<string>();
            AddLibraryCandidates(candidates, abiName, nativeName);

            foreach (var path in candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (NativeLibrary.TryLoad(path, out var loaded))
                {
                    _laplaceNativeHandle = loaded;
                    return _laplaceNativeHandle;
                }
            }

            if (NativeLibrary.TryLoad("laplace_engine_abi", assembly, searchPath, out var directAbi))
            {
                _laplaceNativeHandle = directAbi;
                return _laplaceNativeHandle;
            }

            if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var directNative))
            {
                _laplaceNativeHandle = directNative;
                return _laplaceNativeHandle;
            }

            return IntPtr.Zero;
        }
    }

    private static void AddLibraryCandidates(List<string> candidates, string abiName, string nativeName)
    {
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            candidates.Add(Path.Combine(baseDir, abiName));
            candidates.Add(Path.Combine(baseDir, nativeName));
        }

        var subDir = OperatingSystem.IsWindows() ? "bin" : "lib";
        var envPkg = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(envPkg))
            candidates.Add(Path.Combine(envPkg, subDir, abiName));

        var envBuild = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_BUILD");
        if (!string.IsNullOrWhiteSpace(envBuild))
            candidates.Add(Path.Combine(envBuild, abiName));

        if (LaplaceInstall.TryFindRepoRoot(out var repoRoot))
        {
            var lockFile = Path.GetFullPath(Path.Combine(repoRoot, "..", "Laplace-Operations", "components.lock.tsv"));
            var buildRoot = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_BUILD")
                ?? (OperatingSystem.IsWindows() ? @"D:\Libraries\build\Laplace" : Path.GetFullPath(Path.Combine(repoRoot, "..", "build")));
            var packagesRoot = Path.Combine(buildRoot, "packages", "laplace-engine");
            if (File.Exists(lockFile) && Directory.Exists(packagesRoot))
            {
                var match = Regex.Match(File.ReadAllText(lockFile), @"(?m)^engine\tLaplace-Engine\t([0-9a-f]+)");
                if (match.Success)
                {
                    foreach (var dir in Directory.GetDirectories(packagesRoot, match.Groups[1].Value + "-*"))
                        candidates.Add(Path.Combine(dir, subDir, abiName));
                }
            }

            candidates.Add(Path.Combine(buildRoot, "Laplace-Engine", "icx-release", abiName));
        }

        var nativePkg = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_NATIVE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(nativePkg))
            candidates.Add(Path.Combine(nativePkg, subDir, nativeName));
    }
}
