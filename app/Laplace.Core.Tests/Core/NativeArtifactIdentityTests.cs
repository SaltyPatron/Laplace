using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// The native libraries this process maps are the ones built from this tree, so every
/// native-backed parity assertion describes the source under test. The system loader path
/// can include /opt/laplace/lib, which would otherwise resolve an installed copy instead.
/// </summary>
public sealed class NativeArtifactIdentityTests
{
    private readonly ITestOutputHelper _out;
    public NativeArtifactIdentityTests(ITestOutputHelper o) => _out = o;

    private const string Lib = "liblaplace_core.so";

    /// The paths this process actually mapped, read from /proc/self/maps.
    private static string[] LoadedPaths(string library)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines("/proc/self/maps"))
        {
            int slash = line.IndexOf('/');
            if (slash < 0) continue;
            string path = line[slash..].Trim();
            if (Path.GetFileName(path).StartsWith(library, StringComparison.Ordinal)) paths.Add(path);
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    [Fact]
    public void LoadedNativeLibrary_IsTheOneBuiltFromThisTree()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Force the load: nothing maps the library until a native entry point is called.
        long neutral = Glicko2.NeutralMuFp1e9();
        Assert.True(neutral > 0);

        string loaded = Assert.Single(LoadedPaths(Lib));
        _out.WriteLine($"loaded: {loaded}");

        // Build outputs may be outside the checkout (LAPLACE_BUILD_ROOT).
        string repo = typeof(NativeArtifactIdentityTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "LaplaceRepoRoot").Value!;
        string engineBuild = Environment.GetEnvironmentVariable("LAPLACE_ENGINE_BUILD") is { Length: > 0 } e
            ? e : Path.Combine(repo, "build");
        string built = Path.Combine(engineBuild, "engine", "core", Lib);
        Assert.True(File.Exists(built), $"missing native build artifact: {built}");

        string loadedSha = Sha(loaded!), builtSha = Sha(built);
        _out.WriteLine($"built : {built}");
        _out.WriteLine($"loaded sha {loadedSha[..16]}  built sha {builtSha[..16]}");

        Assert.True(loadedSha == builtSha,
            $"the mapped {Lib} is NOT the one built from this tree.\n"
            + $"  loaded: {loaded} ({loadedSha[..16]})\n"
            + $"  built : {built} ({builtSha[..16]})\n"
            + "Every native-backed assertion in this run is describing the loaded artifact, "
            + "not the source under test. Install the build (pipeline.sh install) or point "
            + "the loader at build/engine/core before trusting a native parity result.");
    }

    [Fact]
    public void NativeDependenciesAndManagedImportsShareTheAppLocalClosure()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Core, dynamics and synthesis each map exactly one image, from the app-local
        // directory, after their own initialization has run.
        Assert.NotEmpty(Laplace.Engine.Dynamics.NativeInterop.LaplaceDynamicsVersion());
        Assert.NotEmpty(Laplace.Engine.Synthesis.NativeInterop.LaplaceSynthesisVersion());
        Assert.NotEmpty(NativeInterop.LaplaceCoreVersion());
        foreach (string library in new[] { "core", "dynamics", "synthesis" })
        {
            string name = $"liblaplace_{library}.so";
            string actual = Assert.Single(LoadedPaths(name));
            string expected = Path.Combine(AppContext.BaseDirectory, name);
            Assert.Equal(expected, actual);
            _out.WriteLine($"{library}: {actual}; sha256={Sha(actual)}");
        }
    }
}
