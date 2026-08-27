using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// A test must execute the artifact built from the source under test.
///
/// /etc/ld.so.conf.d puts /opt/laplace/lib on the system loader path, so a managed test
/// resolves liblaplace_core.so to the INSTALLED copy and never to build/engine/core.
/// Measured 2026-08-24: LAPLACE_GLICKO2_NEUTRAL_MU_FP was changed 1500 -> 1400 and the
/// library relinked, and NeutralMu_MatchesServerConstant stayed GREEN. That test asserts a
/// hard literal 1_500_000_000_000L and would have caught the change; it was reading a copy
/// installed 21 minutes earlier. Installed 01:43, built 02:04, byte-different.
///
/// So every native-backed parity assertion in this repo -- Glicko2FoldParity,
/// ConsensusKeysParity, CollapseIndexParity, QkPairsThresholdParity, RootIdNativeParityProbe
/// -- can pass against a stale installed library while the source defining the invariant is
/// broken. Green proves the INSTALLED binary is consistent; it says nothing about the tree.
///
/// This makes that condition a failure rather than a silent pass.
/// </summary>
public sealed class NativeArtifactIdentityTests
{
    private readonly ITestOutputHelper _out;
    public NativeArtifactIdentityTests(ITestOutputHelper o) => _out = o;

    private const string Lib = "liblaplace_core.so";

    /// The path the process actually mapped, from /proc/self/maps — not a guess, and not
    /// the search order the loader was configured with.
    private static string? LoadedPath()
    {
        foreach (string line in File.ReadLines("/proc/self/maps"))
        {
            int slash = line.IndexOf('/');
            if (slash < 0) continue;
            string path = line[slash..].Trim();
            if (path.Contains(Lib, StringComparison.Ordinal)) return path;
        }
        return null;
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

        string? loaded = LoadedPath();
        Assert.True(loaded is not null, $"{Lib} is not mapped after calling into it");
        _out.WriteLine($"loaded: {loaded}");

        // Build outputs may be outside the checkout (LAPLACE_BUILD_ROOT).
        string repo = typeof(NativeArtifactIdentityTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "LaplaceRepoRoot").Value!;
        string built = Path.Combine(repo, "build", "engine", "core", Lib);
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
}
