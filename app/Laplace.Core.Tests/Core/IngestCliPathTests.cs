using Laplace.Ops;
using Xunit;

namespace Laplace.Core.Tests;

public sealed class IngestCliPathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "laplace-cli-path-" + Guid.NewGuid().ToString("N"));
    private static string Exe => OperatingSystem.IsWindows() ? "Laplace.Cli.exe" : "Laplace.Cli";

    private string FileAt(params string[] parts)
    {
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "path fixture");
        return path;
    }

    [Fact]
    public void ExplicitSelectionWinsOverInstalledAndBuildCopies()
    {
        var selected = FileAt("configured cli", Exe);
        FileAt("installed", "cli", Exe);
        Assert.Equal(selected, IngestProcessRunner.ResolveCliBinary(selected, Path.Combine(root, "installed"), null, null));
    }

    [Fact]
    public void MissingExplicitSelectionDoesNotFallBackToAnotherRelease()
    {
        FileAt("installed", "cli", Exe);
        Assert.Null(IngestProcessRunner.ResolveCliBinary(Path.Combine(root, "missing", Exe), Path.Combine(root, "installed"), null, null));
    }

    [Fact]
    public void InstalledReleaseNeedsNoSourceCheckout()
    {
        var installed = FileAt("installed", "cli", Exe);
        Assert.Equal(installed, IngestProcessRunner.ResolveCliBinary(null, Path.Combine(root, "installed"), null, null));
    }

    [Fact]
    public void DeclaredExternalBuildRootResolvesThePublishedCli()
    {
        var published = FileAt("external build", "app", "bin", "Laplace.Cli", "Release", "net10.0-r2r", Exe);
        Assert.Equal(published, IngestProcessRunner.ResolveCliBinary(null, Path.Combine(root, "installed"), Path.Combine(root, "external build"), null));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
