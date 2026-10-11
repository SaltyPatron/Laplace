using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[CollectionDefinition("Installation paths", DisableParallelization = true)]
public sealed class InstallationPathsCollection;

[Collection("Installation paths")]
public sealed class LaplaceInstallPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "laplace-install-paths-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _prior = new();

    public LaplaceInstallPathTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var name in new[] { "LAPLACE_DATA", "LAPLACE_DATA_ROOT", "LAPLACE_BUILD_ROOT", "LAPLACE_ENGINE_BUILD",
                     "LAPLACE_PERFCACHE_BIN", "LAPLACE_INSTALL_PREFIX", "LAPLACE_OUT", "HF_HUB_CACHE", "HF_HOME" })
        {
            _prior[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _prior) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void CorpusUsesDeclaredDataInsteadOfOperationalStateOrSourceDrive()
    {
        var corpus = Directory.CreateDirectory(Path.Combine(_root, "corpus with spaces")).FullName;
        Environment.SetEnvironmentVariable("LAPLACE_DATA", corpus);
        Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", _root);
        Assert.Equal(corpus, LaplaceInstall.ResolveIngestRoot());
        Assert.Equal(Path.Combine(corpus, "CILI"), LaplaceInstall.ResolveCiliDir());
    }

    [Fact]
    public void MissingCorpusDeclarationCannotSelectAHostSpecificDirectory()
    {
        Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", _root);
        Assert.Throws<InvalidOperationException>(() => LaplaceInstall.ResolveIngestRoot());
    }

    [Fact]
    public void MissingSelectedCorpusCannotFallBack()
    {
        Environment.SetEnvironmentVariable("LAPLACE_DATA", Path.Combine(_root, "missing"));
        Assert.Throws<DirectoryNotFoundException>(() => LaplaceInstall.ResolveIngestRoot());
    }

    [Fact]
    public void InstalledArtifactUsesDeclaredPrefix()
    {
        var share = Directory.CreateDirectory(Path.Combine(_root, "share", "laplace")).FullName;
        var artifact = Path.Combine(share, "laplace_t0_perfcache.bin");
        File.WriteAllBytes(artifact, [1]);
        Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", _root);
        Assert.Equal(artifact, LaplaceInstall.ResolveT0Perfcache());
        Environment.SetEnvironmentVariable("LAPLACE_PERFCACHE_BIN", Path.Combine(_root, "missing.bin"));
        Assert.Throws<FileNotFoundException>(() => LaplaceInstall.ResolveT0Perfcache());
    }

    [Fact]
    public void InstalledRuntimeDoesNotInventABuildDirectory()
    {
        Assert.False(LaplaceInstall.TryDefaultBuildRoot(out var absent));
        Assert.Empty(absent);
        Assert.Throws<InvalidOperationException>(() => LaplaceInstall.DefaultBuildRoot);
        Environment.SetEnvironmentVariable("LAPLACE_BUILD_ROOT", _root);
        Assert.True(LaplaceInstall.TryDefaultBuildRoot(out var selected));
        Assert.Equal(_root, selected);
    }

    [Fact]
    public void ModelCacheUsesStandardHuggingFaceSelection()
    {
        var homeHub = Directory.CreateDirectory(Path.Combine(_root, "hub")).FullName;
        Environment.SetEnvironmentVariable("HF_HOME", _root);
        Assert.Equal(homeHub, LaplaceInstall.ResolveModelHub());
        var explicitCache = Directory.CreateDirectory(Path.Combine(_root, "selected")).FullName;
        Environment.SetEnvironmentVariable("HF_HUB_CACHE", explicitCache);
        Assert.Equal(explicitCache, LaplaceInstall.ResolveModelHub());
        Environment.SetEnvironmentVariable("HF_HUB_CACHE", Path.Combine(_root, "missing"));
        Assert.Throws<DirectoryNotFoundException>(() => LaplaceInstall.ResolveModelHub());
    }

    [Fact]
    public void ExportRequiresOutputOrBuildDeclarationWithoutCreatingBinaryNeighbors()
    {
        Assert.Throws<InvalidOperationException>(() => LaplaceInstall.ResolveGgufOutputDir());
        Environment.SetEnvironmentVariable("LAPLACE_OUT", _root);
        Assert.Equal(Path.Combine(_root, "models"), LaplaceInstall.ResolveGgufOutputDir());
    }
}
