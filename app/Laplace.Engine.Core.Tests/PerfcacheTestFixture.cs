using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// Loads the T0 codepoint perf-cache once for tests that exercise the engine
/// text-decomposition path. After the build decoupled liblaplace_core from
/// the UCD source (the UAX#29/UAX#15 state machines read properties from the
/// runtime-loaded perf-cache, not compiled-in tables), any consumer of
/// <see cref="TextDecomposer"/> / HashComposer must load the blob first — this fixture
/// is the test-side of that contract.
///
/// Locates the blob via (1) <c>LAPLACE_PERFCACHE_BIN</c>, (2) the installed
/// <c>/opt/laplace/share/laplace/</c>, (3) a <c>build*/**/perfcache/</c> tree
/// under the repo. Fails loud if none is found — the blob is a genuine
/// prerequisite (built by <c>just build</c> / the <c>laplace_t0_perfcache</c>
/// target), not an optional fixture.
/// </summary>
public sealed class PerfcacheTestFixture : IDisposable
{
    public string BlobPath { get; }

    public PerfcacheTestFixture()
    {
        BlobPath = LocateBlob()
            ?? throw new InvalidOperationException(
                "T0 perf-cache blob not found. Build it (`just build`, or the engine " +
                "target `laplace_t0_perfcache`) or set LAPLACE_PERFCACHE_BIN. Looked at: " +
                "$LAPLACE_PERFCACHE_BIN, /opt/laplace/share/laplace/, and build*/**/perfcache/.");
        CodepointPerfcache.Load(BlobPath);
    }

    public void Dispose() => CodepointPerfcache.Unload();

    private static string? LocateBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        const string share = "/opt/laplace/share/laplace";
        if (Directory.Exists(share))
        {
            var hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin").FirstOrDefault();
            if (hit is not null) return hit;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory
                    .EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        return null;
    }
}

/// <summary>Shared across every test class that needs the perf-cache loaded.
/// Process-wide native global state ⇒ one load for the whole collection.</summary>
[CollectionDefinition("Perfcache")]
public sealed class PerfcacheCollection : ICollectionFixture<PerfcacheTestFixture> { }
