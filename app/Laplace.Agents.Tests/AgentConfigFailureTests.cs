using Laplace.Agents;
using Xunit;

namespace Laplace.Agents.Tests;

public sealed class AgentConfigFailureTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "laplace-agent-config-failure-" + Guid.NewGuid().ToString("N"));

    public AgentConfigFailureTests() => Directory.CreateDirectory(_work);

    [Fact]
    public void SelectedConfigDisappearingBeforeReadDoesNotBecomeEmbeddedDefaults()
    {
        string selected = Path.Combine(_work, "agents.json");
        File.WriteAllText(selected, "{}");
        string? discovered = AgentCatalog.DiscoverConfigPath(key => key == "LAPLACE_AGENTS_CONFIG" ? selected : null);
        File.Delete(selected);
        var failure = Assert.Throws<AgentException>(() => AgentCatalog.Load(discovered, _ => null));
        Assert.IsType<FileNotFoundException>(failure.InnerException);
    }

    [Fact]
    public void DirectorySelectedAsConfigurationIsAnExplicitFailure()
    {
        var failure = Assert.Throws<AgentException>(() => AgentCatalog.DiscoverConfigPath(
            key => key == "LAPLACE_AGENTS_CONFIG" ? _work : null));
        Assert.Contains("directory", failure.Message);
    }

    [Fact]
    public void ValidEmptyConfigurationStillLoadsEmbeddedDefaults()
    {
        string selected = Path.Combine(_work, "agents.json");
        File.WriteAllText(selected, "{}");
        var catalog = AgentCatalog.Load(selected, _ => null);
        Assert.Equal(selected, catalog.ConfigPath);
        Assert.NotEmpty(catalog.Describe());
    }

    public void Dispose() => Directory.Delete(_work, true);
}
