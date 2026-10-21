using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;

namespace Laplace.Cli;

/// <summary>Human operations through the selected authenticated service.</summary>
internal static class ApiClientCommands
{
    private const string Usage = """
        laplace-mono ops [name-pattern]
        laplace-mono op <name> [arguments-json | @arguments-file] [--max-rows N] [--timeout-seconds N]
        laplace-mono ingest document <file-or-folder> [--verify]
        laplace-mono content put <file> [--code]
        laplace-mono content get <file-id>
        laplace-mono content export <file-id> <destination>
        laplace-mono chat [--session <session>] <prompt>
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            await Console.Out.WriteLineAsync(Usage);
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var origin = ParseOrigin(Environment.GetEnvironmentVariable("LAPLACE_API_URL")!);
            var keyPath = Environment.GetEnvironmentVariable("LAPLACE_API_KEY_FILE");
            if (string.IsNullOrWhiteSpace(keyPath) || !Path.IsPathFullyQualified(keyPath))
                throw new ArgumentException("The API profile requires an absolute LAPLACE_API_KEY_FILE path.");
            var key = (await File.ReadAllTextAsync(keyPath, cancellation.Token)).Trim();
            if (key.Length == 0 || key.Any(char.IsWhiteSpace))
                throw new ArgumentException("The API credential file must contain one nonempty API key.");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler) { BaseAddress = origin, Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return await RunAsync(args, client, Console.Out, Console.Error, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Operation canceled. A submitted write may have completed; inspect its result before retrying.");
            return 130;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException)
        {
            CliRuntime.LoggerFactory.CreateLogger("api-client").LogError(ex, "API client command failed.");
            await Console.Error.WriteLineAsync("laplace-mono: " + ex.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static Uri ParseOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("The API address must be an HTTPS origin without credentials, a path, query, or fragment.");
        return uri;
    }

    internal static async Task<int> RunAsync(
        string[] args, HttpClient client, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                await output.WriteLineAsync(Usage);
                return 0;
            }

            switch (args[0])
            {
                case "ops":
                    if (args.Length > 2) throw new ArgumentException("Usage: laplace-mono ops [name-pattern]");
                    await PrintAsync(client, HttpMethod.Get,
                        "/v1/ops/catalog" + (args.Length == 2 ? "?like=" + Uri.EscapeDataString(args[1]) : ""),
                        null, output, ct);
                    return 0;
                case "op":
                    await InvokeOperationAsync(args, client, output, ct);
                    return 0;
                case "ingest" when args.Length >= 2 && args[1] == "document":
                    await IngestDocumentsAsync(args, client, output, ct);
                    return 0;
                case "content":
                    await ContentAsync(args, client, output, ct);
                    return 0;
                case "chat":
                    await ChatAsync(args, client, output, ct);
                    return 0;
                default:
                    throw new ArgumentException(
                        $"'{args[0]}' is not an API client command. Use 'ops' to discover installed operations or '--help' for client commands. No database connection was attempted.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or JsonException)
        {
            CliRuntime.LoggerFactory.CreateLogger("api-client").LogError(ex, "API operation failed.");
            await error.WriteLineAsync("laplace-mono: " + ex.Message);
            return 1;
        }
    }

    private static async Task InvokeOperationAsync(string[] args, HttpClient client, TextWriter output, CancellationToken ct)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            throw new ArgumentException("Usage: laplace-mono op <name> [arguments-json | @arguments-file] [--max-rows N] [--timeout-seconds N]");
        JsonElement? arguments = null;
        int? maxRows = null, timeoutSeconds = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] is "--max-rows" or "--timeout-seconds")
            {
                var option = args[i++];
                if (i == args.Length || !int.TryParse(args[i], out var number)
                    || number < (option == "--max-rows" ? 1 : 0))
                    throw new ArgumentException($"{option} requires {(option == "--max-rows" ? "a positive" : "a nonnegative")} integer.");
                if (option == "--max-rows") maxRows = number; else timeoutSeconds = number;
            }
            else
            {
                if (arguments is not null) throw new ArgumentException("Supply one JSON object for operation arguments.");
                var json = args[i].StartsWith('@')
                    ? await File.ReadAllTextAsync(args[i][1..], ct) : args[i];
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Operation arguments must be a JSON object.");
                arguments = document.RootElement.Clone();
            }
        }
        await PrintAsync(client, HttpMethod.Post, "/v1/op",
            JsonContent.Create(new { name = args[1], args = arguments, max_rows = maxRows, timeout_seconds = timeoutSeconds }),
            output, ct);
    }

    private static async Task ChatAsync(string[] args, HttpClient client, TextWriter output, CancellationToken ct)
    {
        string? session = null;
        var promptStart = 1;
        if (args.Length > 1 && args[1] == "--session")
        {
            if (args.Length < 4 || string.IsNullOrWhiteSpace(args[2]))
                throw new ArgumentException("Usage: laplace-mono chat [--session <session>] <prompt>");
            session = args[2];
            promptStart = 3;
        }
        var prompt = string.Join(" ", args.Skip(promptStart));
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Usage: laplace-mono chat [--session <session>] <prompt>");
        await PrintAsync(client, HttpMethod.Post, "/v1/chat/completions",
            JsonContent.Create(new { model = "laplace-converse-001", session,
                messages = new[] { new { role = "user", content = prompt } }, stream = false }),
            output, ct);
    }

    private static async Task IngestDocumentsAsync(string[] args, HttpClient client, TextWriter output, CancellationToken ct)
    {
        if (args.Length is < 3 or > 4 || (args.Length == 4 && args[3] != "--verify"))
            throw new ArgumentException("Usage: laplace-mono ingest document <file-or-folder> [--verify]");
        var selected = Path.GetFullPath(args[2]);
        bool directory = Directory.Exists(selected);
        if (!directory && !File.Exists(selected)) throw new FileNotFoundException("Document path does not exist.", selected);
        var basePath = directory ? selected : Path.GetDirectoryName(selected)!;
        IEnumerable<string> files = directory
            ? Directory.EnumerateFiles(selected, "*", new EnumerationOptions {
                RecurseSubdirectories = true, IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(IsDocument)
                .Where(path => !VendoredPathFilter.IsVendoredOrBuildLocation(path, selected))
                .Order(StringComparer.Ordinal)
            : new[] { selected };
        int completed = 0;
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsDocument(path))
                throw new ArgumentException("Document ingestion accepts UTF-8 .txt, .md, and .markdown files.");
            var receipt = await UploadAsync(client, path, Path.GetRelativePath(basePath, path).Replace('\\', '/'), false, ct);
            if (args.Length == 4)
            {
                var id = receipt.GetProperty("file_id").GetString()!;
                using var response = await SendAsync(client, HttpMethod.Get, ContentPath(id) + "/raw", null, ct);
                await using var remote = await response.Content.ReadAsStreamAsync(ct);
                await using var local = File.OpenRead(path);
                var remoteDigest = await System.Security.Cryptography.SHA256.HashDataAsync(remote, ct);
                var localDigest = await System.Security.Cryptography.SHA256.HashDataAsync(local, ct);
                if (!remoteDigest.AsSpan().SequenceEqual(localDigest))
                    throw new InvalidDataException($"Persisted content differs from '{path}'.");
            }
            await output.WriteLineAsync(receipt.GetRawText());
            completed++;
        }
        if (completed == 0) throw new ArgumentException("No .txt, .md, or .markdown documents were found.");
    }

    private static bool IsDocument(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".markdown";

    private static async Task<JsonElement> UploadAsync(
        HttpClient client, string path, string relativePath, bool code, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        using var body = new StreamContent(file);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        string target = "/v1/content/" + (code ? "code" : "text") + "/raw?name="
            + Uri.EscapeDataString(Path.GetFileName(path)) + "&path=" + Uri.EscapeDataString(relativePath)
            + "&modified_at=" + Uri.EscapeDataString(File.GetLastWriteTimeUtc(path).ToString("O"));
        using var response = await SendAsync(client, HttpMethod.Post, target, body, ct);
        using var receipt = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (receipt.RootElement.ValueKind != JsonValueKind.Object
            || !receipt.RootElement.TryGetProperty("file_id", out var fileId)
            || fileId.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The service did not return a content admission identity.");
        _ = ContentPath(fileId.GetString()!);
        return receipt.RootElement.Clone();
    }

    private static async Task ContentAsync(string[] args, HttpClient client, TextWriter output, CancellationToken ct)
    {
        if (args.Length >= 3 && args[1] == "put")
        {
            if (args.Length > 4 || (args.Length == 4 && args[3] != "--code"))
                throw new ArgumentException("Usage: laplace-mono content put <file> [--code]");
            var path = Path.GetFullPath(args[2]);
            var receipt = await UploadAsync(client, path, Path.GetFileName(path), args.Length == 4, ct);
            await output.WriteLineAsync(receipt.GetRawText());
            return;
        }
        if (args.Length == 3 && args[1] == "get")
        {
            await PrintAsync(client, HttpMethod.Get, ContentPath(args[2]), null, output, ct);
            return;
        }
        if (args.Length == 4 && args[1] == "export")
        {
            using var response = await SendAsync(client, HttpMethod.Get, ContentPath(args[2]) + "/raw", null, ct);
            // Never replace an existing user file, including on a failed transfer.
            var destination = Path.GetFullPath(args[3]);
            await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try { await response.Content.CopyToAsync(file, ct); }
            catch
            {
                await file.DisposeAsync();
                File.Delete(destination);
                throw;
            }
            return;
        }
        throw new ArgumentException("Usage: laplace-mono content <put|get|export> ...; see --help.");
    }

    private static string ContentPath(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit))
            throw new ArgumentException("Content identity must contain exactly 32 hexadecimal characters.");
        return "/v1/content/" + id.ToLowerInvariant();
    }

    private static async Task PrintAsync(
        HttpClient client, HttpMethod method, string path, HttpContent? body, TextWriter output, CancellationToken ct)
    {
        using var response = await SendAsync(client, method, path, body, ct);
        await output.WriteLineAsync(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new HttpRequestException(status is >= 300 and < 400
            ? "The selected API redirected the request. Update the profile to the intended HTTPS origin; credentials were not forwarded."
            : $"The selected API returned HTTP {status}. The operation was not reported as successful.",
            null, (HttpStatusCode)status);
    }
}
