using System.Reflection;
using Laplace.Data;
using Laplace.Hosting;
using Xunit;

namespace Laplace.Data.Tests;

public sealed class ReadPathArchitectureGateTests
{
    private static string ResolveRepoRoot()
    {
        var attr = typeof(ReadPathArchitectureGateTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LaplaceRepoRoot")?.Value;
        return attr ?? throw new InvalidOperationException("LaplaceRepoRoot assembly metadata is missing.");
    }

    [Fact]
    public void LaplaceData_HoldsNoLegacySchemaOrWriterSymbols()
    {
        var dataDir = Path.Combine(ResolveRepoRoot(), "app", "Laplace.Data");
        Assert.True(Directory.Exists(dataDir));

        string[] forbidden =
        [
            "laplace_substrate",
            "laplace_geom",
            "laplace.entities",
            "laplace.physicality",
            "consensus.claim_standings",
            "ISubstrateWriter",
            "converse.",
            "browse.",
            "taxonomy.",
            "structural.",
            "generation.",
            "realize.",
            "lexical.",
            "inspection.",
        ];

        foreach (var file in Directory.EnumerateFiles(dataDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
            {
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ActiveManagedProjects_ContainNoHandWrittenPInvoke()
    {
        var appDir = Path.Combine(ResolveRepoRoot(), "app");
        string[] activeDirs =
        [
            "Laplace.Core",
            "Laplace.Ops",
            "Laplace.Hosting",
            "Laplace.Data",
            "Laplace.Data.Tests",
        ];

        var rx = new System.Text.RegularExpressions.Regex(@"\[(?:LibraryImport|DllImport)\s*\(");
        foreach (var dirName in activeDirs)
        {
            var dir = Path.Combine(appDir, dirName);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                Assert.False(rx.IsMatch(text), $"Unexpected hand-written P/Invoke attribute in {file}");
            }
        }
    }

    [Fact]
    public void LaplaceDataSource_BoundsServingCommandTimeoutAndDisablesAutoPrepare()
    {
        var serving = LaplaceDataSource.ConnectionStringFor(
            SubstrateAccess.Serving,
            "Host=127.0.0.1;Database=laplace;Username=laplace;Command Timeout=0");
        Assert.Contains("Command Timeout=120", serving, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Max Auto Prepare=0", serving, StringComparison.OrdinalIgnoreCase);

        var ingest = LaplaceDataSource.ConnectionStringFor(
            SubstrateAccess.Ingest,
            "Host=127.0.0.1;Database=laplace;Username=laplace;Command Timeout=0");
        Assert.Contains("Command Timeout=0", ingest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Max Auto Prepare=0", ingest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagedServiceState_DetectsStopMarkerFileOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "laplace-stop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var missing = Path.Combine(tempDir, "api.stopped");
            Assert.False(ManagedServiceState.OperatorStoppedAtPath(missing));

            File.WriteAllText(missing, "stopped");
            Assert.True(ManagedServiceState.OperatorStoppedAtPath(missing));

            var dirMarker = Path.Combine(tempDir, "dir.stopped");
            Directory.CreateDirectory(dirMarker);
            Assert.Throws<InvalidDataException>(() => ManagedServiceState.OperatorStoppedAtPath(dirMarker));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
