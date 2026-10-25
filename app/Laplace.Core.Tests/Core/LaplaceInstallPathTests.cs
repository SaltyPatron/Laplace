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
        foreach (var name in new[] { "LAPLACE_MACHINE_ENV", "LAPLACE_DATA", "LAPLACE_WORK", "LAPLACE_MODELS", "LAPLACE_BUILD", "LAPLACE_BUILD_ROOT", "LAPLACE_ENGINE_BUILD",
                     "LAPLACE_TIER0", "LAPLACE_PERFCACHE_BIN", "LAPLACE_INSTALL_PREFIX", "LAPLACE_OUT", "HF_HUB_CACHE", "HF_HOME" })
        {
            _prior[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        Environment.SetEnvironmentVariable("LAPLACE_MACHINE_ENV", Path.Combine(_root, "absent-machine.env"));
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
        Environment.SetEnvironmentVariable("LAPLACE_WORK", _root);
        Assert.Equal(corpus, LaplaceInstall.ResolveIngestRoot());
        Assert.Equal(Path.Combine(corpus, "CILI"), LaplaceInstall.ResolveCiliDir());
    }

    [Fact]
    public void MissingCorpusDeclarationCannotSelectAHostSpecificDirectory()
    {
        Environment.SetEnvironmentVariable("LAPLACE_WORK", _root);
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
        var artifact = Path.Combine(share, "tier0.bin");
        File.WriteAllBytes(artifact, [1]);
        Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", _root);
        Assert.Equal(artifact, LaplaceInstall.ResolveT0Perfcache());
        Environment.SetEnvironmentVariable("LAPLACE_TIER0", Path.Combine(_root, "missing.bin"));
        Assert.Throws<FileNotFoundException>(() => LaplaceInstall.ResolveT0Perfcache());
    }

    [Fact]
    public void LegacyPerfcacheSettingCannotOverrideTheInstalledNativeFloor()
    {
        var share = Directory.CreateDirectory(Path.Combine(_root, "share", "laplace")).FullName;
        var native = Path.Combine(share, "tier0.bin");
        var legacy = Path.Combine(_root, "laplace_t0_perfcache_17.0.0.bin");
        File.WriteAllBytes(native, [1]);
        File.WriteAllBytes(legacy, [2]);
        Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", _root);
        Environment.SetEnvironmentVariable("LAPLACE_PERFCACHE_BIN", legacy);
        Assert.Equal(native, LaplaceInstall.ResolveT0Perfcache());
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

    [Fact]
    public void MachineEnvDeclarationSuppliesCorpusAndBuildRootWhenProcessEnvIsUnset()
    {
        var corpus = Directory.CreateDirectory(Path.Combine(_root, "declared-corpus")).FullName;
        var machineEnv = Path.Combine(_root, "machine.env");
        File.WriteAllLines(machineEnv,
        [
            "# Installer-generated machine declaration",
            $"LAPLACE_DATA={corpus}",
            $"LAPLACE_BUILD_ROOT={_root}",
        ]);
        Environment.SetEnvironmentVariable("LAPLACE_MACHINE_ENV", machineEnv);

        Assert.Equal(corpus, LaplaceInstall.ResolveIngestRoot());
        Assert.True(LaplaceInstall.TryDefaultBuildRoot(out var buildRoot));
        Assert.Equal(_root, buildRoot);
    }

    [Fact]
    public void ModelsRootRequiresItsOwnDeclaration()
    {
        var corpus = Directory.CreateDirectory(Path.Combine(_root, "Data")).FullName;
        var models = Directory.CreateDirectory(Path.Combine(_root, "CustomModels")).FullName;
        Environment.SetEnvironmentVariable("LAPLACE_DATA", corpus);
        Assert.Throws<InvalidOperationException>(() => LaplaceInstall.ResolveModelsRoot());

        Environment.SetEnvironmentVariable("LAPLACE_MODELS", models);
        Assert.Equal(models, LaplaceInstall.ResolveModelsRoot());
        Assert.Equal(Path.Combine(models, "stack-v2"), LaplaceInstall.ResolvePathUnderModels("stack-v2"));

        Environment.SetEnvironmentVariable("LAPLACE_MODELS", Path.Combine(_root, "missing-models"));
        Assert.Throws<DirectoryNotFoundException>(() => LaplaceInstall.ResolveModelsRoot());
    }
}
