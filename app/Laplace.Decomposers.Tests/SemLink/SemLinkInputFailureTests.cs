using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.SemLink.Tests;

public sealed class SemLinkInputFailureTests
{
    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"first\":1,\"broken\":")]
    [InlineData("{\"first\":1} trailing")]
    public async Task InvalidSourceCannotProduceASuccessfulEmptyOrPartialRecordCount(string contents)
    {
        await WithInputAsync(contents, async path => Assert.NotEqual(0, await ExecuteAsync(path)));
    }

    [Fact]
    public async Task MissingDeclaredSourceRemainsAFileFailure()
    {
        string path = Path.Combine(Path.GetTempPath(), "semlink-missing-" + Guid.NewGuid().ToString("N"), "pb-vn2.json");
        Assert.NotEqual(0, await ExecuteAsync(path));
    }

    [Fact]
    public async Task PreCanceledRequestCannotReturnSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExecuteAsync("not-opened-after-cancellation.json", cancellation.Token));
    }

    [Fact]
    public async Task EmptyObjectIsAValidZeroRecordSource()
    {
        await WithInputAsync("{}", async path => Assert.Equal(0, await ExecuteAsync(path)));
    }

    [Fact]
    public async Task UnreadableSourceRemainsAnIoFailure()
    {
        await WithInputAsync("{\"mapping\":{}}", async path =>
        {
            if (OperatingSystem.IsWindows())
            {
                using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.NotEqual(0, await ExecuteAsync(path));
            }
            else
            {
                var permissions = File.GetUnixFileMode(path);
                try
                {
                    File.SetUnixFileMode(path, UnixFileMode.None);
                    Assert.NotEqual(0, await ExecuteAsync(path));
                }
                finally { File.SetUnixFileMode(path, permissions); }
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidNestedMappingsAreAccepted(bool utf8Preamble)
    {
        await WithInputAsync((utf8Preamble ? "\ufeff" : "")
            + "{\"abandon.01\":{\"51.2\":{\"ARG0\":\"theme\"}}}",
            async path => Assert.Equal(0, await ExecuteAsync(path)));
    }

    private static async Task<int> ExecuteAsync(string path, CancellationToken ct = default)
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        return await NativeRecipeExecution.Configured().ExecuteAsync(
            new NativeRecipeRequest(NativeRecipeMode.Claims, "semlink", [path], Threads: 1), output, error, ct);
    }

    private static async Task WithInputAsync(string contents, Func<string, Task> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "semlink-native-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "pb-vn2.json");
        try { await File.WriteAllTextAsync(path, contents); await check(path); }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}
