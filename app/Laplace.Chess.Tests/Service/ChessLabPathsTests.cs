using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessLabPathsTests : IDisposable
{
    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);

    [Fact]
    public void Cutechess_UsesEnvWhenSet()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"cutechess-{Guid.NewGuid():N}.exe");
        File.WriteAllText(fake, "");
        SetEnv(ChessLabPaths.EnvCutechess, fake);

        var probe = ChessLabPaths.Cutechess;

        Assert.Equal("env", probe.Source);
        Assert.Equal(fake, probe.Path);
        Assert.True(probe.Found);
    }

    [Fact]
    public void Stockfish_UsesRepoRelativeWhenRootSet()
    {
        var root = CreateFakeRepo();
        var sf = Path.Combine(root, "build-cutechess", "stockfish.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(sf)!);
        File.WriteAllText(sf, "");
        SetEnv("LAPLACE_ROOT", root);
        ClearEnv(ChessLabPaths.EnvStockfish);

        var probe = ChessLabPaths.ResolveExecutableForTest(
            null,
            r => Path.Combine(r, "build-cutechess", "stockfish.exe"),
            ["stockfish.exe"]);

        Assert.Equal("repo", probe.Source);
        Assert.Equal(sf, probe.Path);
        Assert.True(probe.Found);
    }

    [Fact]
    public void LaplaceUci_DiscoversOnPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"laplace-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var uci = Path.Combine(dir, "laplace-uci.exe");
        File.WriteAllText(uci, "");
        ClearEnv(ChessLabPaths.EnvUci);
        SetEnv("PATH", dir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""));

        var probe = ChessLabPaths.ResolveExecutableForTest(
            null,
            _ => null,
            ["laplace-uci.exe", "laplace-uci"]);

        Assert.Equal("path", probe.Source);
        Assert.Equal(uci, probe.Path);
        Assert.True(probe.Found);
    }

    [Fact]
    public void QtBin_MissingWhenUnset()
    {
        ClearEnv(ChessLabPaths.EnvQtBin);

        var probe = ChessLabPaths.QtBin;

        Assert.Equal("missing", probe.Source);
        Assert.False(probe.Found);
        Assert.Null(probe.Path);
    }

    [Fact]
    public void LoadEnvFile_SetsUnsetVariables()
    {
        ClearEnv(ChessLabPaths.EnvStockfish);
        var envFile = Path.Combine(Path.GetTempPath(), $"chess-lab-{Guid.NewGuid():N}.env");
        var sf = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.exe");
        File.WriteAllText(envFile, $"{ChessLabPaths.EnvStockfish}={sf}\n");
        try
        {
            ChessLabPaths.LoadEnvFile(envFile);
            Assert.Equal(sf, Environment.GetEnvironmentVariable(ChessLabPaths.EnvStockfish));
        }
        finally
        {
            File.Delete(envFile);
        }
    }

    [Fact]
    public void LoadEnvFile_DoesNotOverrideExisting()
    {
        SetEnv(ChessLabPaths.EnvStockfish, "existing.exe");
        var envFile = Path.Combine(Path.GetTempPath(), $"chess-lab-{Guid.NewGuid():N}.env");
        File.WriteAllText(envFile, $"{ChessLabPaths.EnvStockfish}=from-file.exe\n");
        try
        {
            ChessLabPaths.LoadEnvFile(envFile);
            Assert.Equal("existing.exe", Environment.GetEnvironmentVariable(ChessLabPaths.EnvStockfish));
        }
        finally
        {
            File.Delete(envFile);
        }
    }

    private static string CreateFakeRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"laplace-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "app"));
        Directory.CreateDirectory(Path.Combine(root, "engine"));
        return root;
    }

    private void SetEnv(string key, string? value)
    {
        if (!_saved.ContainsKey(key))
            _saved[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    private void ClearEnv(string key) => SetEnv(key, null);

    public void Dispose()
    {
        foreach (var (key, value) in _saved)
            Environment.SetEnvironmentVariable(key, value);
    }
}
