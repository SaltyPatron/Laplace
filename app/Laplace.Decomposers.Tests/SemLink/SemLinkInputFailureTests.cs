using Laplace.Decomposers.SemLink;
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
        string path = Path.Combine(Path.GetTempPath(), "semlink-invalid-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, contents);
        try
        {
            var failure = await Record.ExceptionAsync(() => SemLinkJsonPairStream.CountRecordsAsync(path, default));
            Assert.NotNull(failure);
            Assert.True(failure is InvalidDataException or System.Text.Json.JsonException, failure.ToString());
            var streamFailure = await Record.ExceptionAsync(() => ReadRecordsAsync(path, default));
            Assert.NotNull(streamFailure);
            Assert.True(streamFailure is InvalidDataException or System.Text.Json.JsonException, streamFailure.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingDeclaredSourceRemainsAFileFailure()
    {
        string path = Path.Combine(Path.GetTempPath(), "semlink-missing-" + Guid.NewGuid().ToString("N") + ".json");
        var failure = await Assert.ThrowsAsync<FileNotFoundException>(() => SemLinkJsonPairStream.CountRecordsAsync(path, default));
        Assert.Equal(path, failure.FileName);
        await Assert.ThrowsAsync<FileNotFoundException>(() => ReadRecordsAsync(path, default));
    }

    [Fact]
    public async Task PreCanceledInventoryCannotReturnASuccessfulUnknownCount()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SemLinkJsonPairStream.CountRecordsAsync("not-opened-after-cancellation.json", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReadRecordsAsync("not-opened-after-cancellation.json", cancellation.Token));
    }

    [Fact]
    public async Task EmptyObjectIsAValidZeroRecordSource()
    {
        string path = Path.Combine(Path.GetTempPath(), "semlink-empty-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{}");
        try
        {
            Assert.Equal(0, await SemLinkJsonPairStream.CountRecordsAsync(path, default));
            Assert.Equal(0, await ReadRecordsAsync(path, default));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UnreadableSourceKeepsItsIoFailureInBothInventoryAndExtraction()
    {
        string path = Path.Combine(Path.GetTempPath(), "semlink-locked-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{\"mapping\":{}}");
        try
        {
            using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await Assert.ThrowsAnyAsync<IOException>(() => SemLinkJsonPairStream.CountRecordsAsync(path, default));
            await Assert.ThrowsAnyAsync<IOException>(() => ReadRecordsAsync(path, default));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidNestedMappingsHaveTheSameInventoryAndNativeRecordCount(bool utf8Preamble)
    {
        string path = Path.Combine(Path.GetTempPath(), "semlink-valid-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, (utf8Preamble ? "\ufeff" : "")
            + "{\"give.01\":{\"13.1\":{\"ARG0\":\"agent\"}},\"other.01\":[\"frame\"]}");
        try
        {
            Assert.Equal(2, await SemLinkJsonPairStream.CountRecordsAsync(path, default));
            Assert.Equal(2, await ReadRecordsAsync(path, default));
        }
        finally { File.Delete(path); }
    }

    private static async Task<int> ReadRecordsAsync(string path, CancellationToken ct)
    {
        int count = 0;
        await foreach (var record in new SemLinkJsonPairStream(path).RecordsAsync(ct))
        {
            using var ast = record.Ast;
            Assert.Equal(count, record.RowIndex);
            Assert.True(ast.Diagnostics.SyntaxComplete);
            using var json = System.Text.Json.JsonDocument.Parse(record.LineUtf8);
            Assert.Single(json.RootElement.EnumerateObject());
            count++;
        }
        return count;
    }
}
