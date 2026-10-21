using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.AgentTrace;
using Laplace.Decomposers.Tests;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Laplace.Decomposers.AgentTrace.Tests;

public sealed class AgentTraceFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "laplace-trace-failures-" + Guid.NewGuid().ToString("N"));

    public AgentTraceFailureTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("lines")]
    [InlineData("head")]
    [InlineData("sqlite")]
    [InlineData("cursor")]
    public void AnUnreadableCandidateCannotBecomeAnUnrecognizedFile(string sniff)
    {
        string path = Path.Combine(_directory, "store.db");
        File.WriteAllText(path, "SQLite format 3\0");
        File.WriteAllText(Path.Combine(_directory, "meta.json"), "{}");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() =>
        {
            switch (sniff)
            {
                case "lines": AdapterJson.FirstLines(path, 2).ToArray(); break;
                case "head": AdapterJson.Head(path); break;
                case "sqlite": SqliteSniff.IsSqlite(path); break;
                case "cursor": new CursorAdapter().CanHandle(path); break;
            }
        });
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("gemini")]
    [InlineData("amp")]
    [InlineData("continue")]
    [InlineData("cline")]
    public async Task CorruptSelectedDocumentsCannotCompleteAsEmptySessions(string provider)
    {
        string path = Path.Combine(_directory, "session.json");
        await File.WriteAllTextAsync(path, "{\"messages\":[{\"role\":\"user\",\"content\":\"private-content-fixture");
        IAgentTraceAdapter adapter = provider switch
        {
            "generic" => new GenericJsonAdapter(), "gemini" => new GeminiAdapter(),
            "amp" => new AmpAdapter(), "continue" => new ContinueAdapter(), _ => new ClineAdapter(),
        };
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Drain(adapter, path));
        Assert.Contains(path, failure.Message);
        Assert.DoesNotContain("private-content-fixture", failure.ToString());
    }

    [Fact]
    public async Task ACorruptJsonlRecordRetainsItsLineInsteadOfDisappearing()
    {
        string path = Path.Combine(_directory, "session.jsonl");
        await File.WriteAllTextAsync(path, "{\"role\":\"user\",\"content\":\"valid\"}\n\n{\"private-content-fixture\":\n");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new GenericJsonAdapter(), path));
        Assert.Contains(path, failure.Message);
        Assert.Contains("line=3", failure.Message);
        Assert.DoesNotContain("private-content-fixture", failure.ToString());
    }

    [Fact]
    public async Task AFailedSqliteOpenCannotCompleteAsAnEmptyContainer()
    {
        string path = Path.Combine(_directory, "missing", "opencode.db");
        await Assert.ThrowsAsync<SqliteException>(() => Drain(new OpenCodeAdapter(), path));
    }

    [Fact]
    public async Task ASelectedZedContainerMustHaveItsDeclaredThreadTable()
    {
        string path = Path.Combine(_directory, "threads.db");
        using (var connection = Open(path)) { }
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new ZedAdapter(), path));
        Assert.Contains("threads", failure.Message);
        Assert.Contains(path, failure.Message);
    }

    [Fact]
    public async Task ACorruptCompressedThreadCannotBecomeAnEmptySuccessfulContainer()
    {
        string path = Path.Combine(_directory, "threads.db");
        using (var connection = Open(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE threads(id TEXT,summary TEXT,updated_at TEXT,data_type TEXT,data BLOB);"
                + "INSERT INTO threads VALUES('thread-fixture',NULL,NULL,'zstd',X'010203');";
            command.ExecuteNonQuery();
        }
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new ZedAdapter(), path));
        Assert.IsType<ZstdSharp.ZstdException>(failure.InnerException);
        Assert.Contains("thread-fixture", failure.Message);
        Assert.Contains(path, failure.Message);
    }

    [Fact]
    public async Task ValidEmptyJsonContainersRemainValidEmptyInputs()
    {
        string path = Path.Combine(_directory, "empty.json");
        await File.WriteAllTextAsync(path, "{\"messages\":[]}", new UTF8Encoding(false));
        await Drain(new GenericJsonAdapter(), path);
        using var parsed = JsonAstDocument.TryParse("{\"messages\":[]}");
        Assert.NotNull(parsed);
        Assert.True(parsed.SyntaxComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMissingOrUnsupportedInputsCannotProduceAnEmptySuccessInventory(bool unsupported)
    {
        string path = Path.Combine(_directory, "selected.bin");
        if (unsupported) await File.WriteAllTextAsync(path, "unrecognized format");
        var decomposer = new AgentTraceDecomposer();
        var context = new FakeContext(path, new NullWriter());
        if (unsupported)
            await Assert.ThrowsAsync<InvalidDataException>(() => decomposer.DescribeInputAsync(context, DecomposerOptions.Default));
        else
            await Assert.ThrowsAsync<FileNotFoundException>(() => decomposer.DescribeInputAsync(context, DecomposerOptions.Default));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task Drain(IAgentTraceAdapter adapter, string path)
    {
        await foreach (var _ in adapter.ParseAsync(path, CancellationToken.None)) { }
    }
}
