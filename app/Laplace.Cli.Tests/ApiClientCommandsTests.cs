using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class ApiClientCommandsTests
{
    [Theory]
    [InlineData("cpu-topology", "cpu-topology")]
    [InlineData("--api-client", "laplace-mono ops")]
    public async Task ExecutableUsesExplicitModeDespiteHostApiEnvironment(string command, string expectedHelp)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add("--help");
        start.Environment["LAPLACE_API_URL"] = "http://127.0.0.1:5187";
        start.Environment["LAPLACE_API_KEY_FILE"] = "not-an-api-profile";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var stdout = await output;
        var stderr = await error;
        Assert.True(process.ExitCode == 0, $"CLI exited {process.ExitCode}: {stderr}");
        Assert.Contains(expectedHelp, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTPS origin", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com/?token=secret")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("file:///C:/secrets")]
    public void CredentialDestinationMustBeAnHttpsOrigin(string value)
        => Assert.Throws<ArgumentException>(() => ApiClientCommands.ParseOrigin(value));

    [Fact]
    public void HttpsOriginRetainsTheSelectedHostAndPort()
        => Assert.Equal("https://example.com:8443/",
            ApiClientCommands.ParseOrigin("https://example.com:8443").AbsoluteUri);

    [Fact]
    public async Task InstalledOperationReceivesTypedArgumentsAndLimits()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/op", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("example.read", body.RootElement.GetProperty("name").GetString());
            Assert.Equal(7, body.RootElement.GetProperty("args").GetProperty("count").GetInt32());
            Assert.True(body.RootElement.GetProperty("args").GetProperty("enabled").GetBoolean());
            Assert.Equal(29, body.RootElement.GetProperty("max_rows").GetInt32());
            Assert.Equal(0, body.RootElement.GetProperty("timeout_seconds").GetInt32());
            return Json("""{"object":"op.result","rows":[{"value":7}]}""");
        });
        using var client = Client(handler);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await ApiClientCommands.RunAsync(
            ["op", "example.read", """{"count":7,"enabled":true}""", "--max-rows", "29", "--timeout-seconds", "0"],
            client, output, error));
        Assert.Contains("\"value\":7", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{invalid")]
    public async Task InvalidArgumentsAreRejectedBeforeSending(string arguments)
    {
        using var handler = new Handler(_ => Task.FromResult(Json("{}")));
        using var client = Client(handler);
        Assert.Equal(1, await ApiClientCommands.RunAsync(["op", "example.read", arguments],
            client, TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task FailedServiceCallIsNotRetriedOrReportedSuccessful(int status)
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var client = Client(handler);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await ApiClientCommands.RunAsync(["op", "example.write", "{}"],
            client, output, error));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("", output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task OperatorOnlyCommandDoesNotFallBackToTheDatabase()
    {
        using var handler = new Handler(_ => Task.FromResult(Json("{}")));
        using var client = Client(handler);
        using var error = new StringWriter();
        Assert.Equal(1, await ApiClientCommands.RunAsync(["rebuild-phys-indexes"], client, TextWriter.Null, error));
        Assert.Equal(0, handler.Calls);
        Assert.Contains("No database connection was attempted", error.ToString());
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("0011?other=1")]
    [InlineData("fffffffffffffffffffffffffffffffz")]
    public async Task ContentIdentityCannotChangeTheRequestTarget(string id)
    {
        using var handler = new Handler(_ => Task.FromResult(Json("{}")));
        using var client = Client(handler);
        Assert.Equal(1, await ApiClientCommands.RunAsync(["content", "get", id],
            client, TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CatalogPatternIsEncodedAsOneQueryValue()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("?like=a%26other%3Dsecret", request.RequestUri!.Query);
            return Task.FromResult(Json("""{"object":"op.catalog","operations":[]}"""));
        });
        using var client = Client(handler);
        Assert.Equal(0, await ApiClientCommands.RunAsync(["ops", "a&other=secret"],
            client, TextWriter.Null, TextWriter.Null));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatUsesPublishedModelAndSelectedConversation(bool resume)
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("laplace-converse-001", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(resume ? "selected-session" : null, body.RootElement.GetProperty("session").GetString());
            Assert.Equal("What about its senses?", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return Json("""{"metadata":{"session":"selected-session"}}""");
        });
        using var client = Client(handler);
        using var output = new StringWriter();
        var args = resume
            ? new[] { "chat", "--session", "selected-session", "What about its senses?" }
            : new[] { "chat", "What about its senses?" };
        Assert.Equal(0, await ApiClientCommands.RunAsync(args, client, output, TextWriter.Null));
        Assert.Contains("selected-session", output.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("chat", "")]
    [InlineData("chat", "--session")]
    [InlineData("chat", "--session", "selected-session")]
    public async Task IncompleteChatDoesNotSendARequest(params string[] args)
    {
        using var handler = new Handler(_ => Task.FromResult(Json("{}")));
        using var client = Client(handler);
        Assert.Equal(1, await ApiClientCommands.RunAsync(args, client, TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, handler.Calls);
    }


    [Fact]
    public async Task DocumentAdmissionVerifiesPersistedBytesAndExcludesDependencyFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "laplace-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var bytes = Encoding.UTF8.GetBytes("Laplace: 日本語\n");
        await File.WriteAllBytesAsync(Path.Combine(directory, "notes.md"), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, ".git", "excluded.md"), "not selected");
        try
        {
            using var handler = new Handler(async request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal("/v1/content/text/raw", request.RequestUri!.AbsolutePath);
                    Assert.Contains("path=notes.md", request.RequestUri.Query);
                    Assert.DoesNotContain("user_id", request.RequestUri.Query);
                    Assert.Equal(bytes, await request.Content!.ReadAsByteArrayAsync());
                    return Json("""{"file_id":"00112233445566778899aabbccddeeff"}""");
                }
                Assert.Equal("/v1/content/00112233445566778899aabbccddeeff/raw", request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            });
            using var client = Client(handler);
            using var output = new StringWriter();
            Assert.Equal(0, await ApiClientCommands.RunAsync(["ingest", "document", directory, "--verify"],
                client, output, TextWriter.Null));
            Assert.Equal(2, handler.Calls);
            Assert.Contains("00112233445566778899aabbccddeeff", output.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedReadbackDoesNotPrintASuccessReceipt()
    {
        var file = Path.Combine(Path.GetTempPath(), "laplace-client-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "original");
        try
        {
            using var handler = new Handler(request => Task.FromResult(request.Method == HttpMethod.Post
                ? Json("""{"file_id":"00112233445566778899aabbccddeeff"}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("different") }));
            using var client = Client(handler);
            using var output = new StringWriter();
            Assert.Equal(1, await ApiClientCommands.RunAsync(["ingest", "document", file, "--verify"],
                client, output, TextWriter.Null));
            Assert.Equal("", output.ToString());
            Assert.Equal(2, handler.Calls);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ExportPreservesAnExistingDestination()
    {
        var file = Path.Combine(Path.GetTempPath(), "laplace-client-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "existing");
        try
        {
            using var handler = new Handler(_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("replacement") }));
            using var client = Client(handler);
            Assert.Equal(1, await ApiClientCommands.RunAsync(["content", "export", "00112233445566778899aabbccddeeff", file],
                client, TextWriter.Null, TextWriter.Null));
            Assert.Equal("existing", await File.ReadAllTextAsync(file));
        }
        finally { File.Delete(file); }
    }

    private static HttpClient Client(Handler handler) => new(handler)
        { BaseAddress = new Uri("https://laplace.example/") };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return send(request);
        }
    }
}
