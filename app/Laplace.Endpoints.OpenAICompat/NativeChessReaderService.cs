using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Thin proxy to the native C <c>lpm</c> HTTP reader for chess database, opening,
/// and tablebase queries. Uses an external loopback service when configured
/// (<c>LAPLACE_MODULAR_HTTP_ENDPOINT</c>) or manages a local <c>lpm serve</c> child
/// wired to this host's verified database connection and bundled <c>share/laplace</c>
/// perf-caches (including <c>tier0.chess</c>).
/// </summary>
internal sealed class NativeChessReaderService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ILogger<NativeChessReaderService> _logger;
    private Process? _process;
    private Uri? _origin;
    private string _token = "";
    private bool _disposed;

    public NativeChessReaderService(ILogger<NativeChessReaderService> logger) => _logger = logger;

    public async Task<JsonElement> ReadAsync(string path, object? body, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        var token = budget.Token;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureStartedAsync(token).ConfigureAwait(false);
            using var req = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(_origin!, path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (body is not null)
                req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Native chess reader {Path} returned HTTP {Status}", path, (int)resp.StatusCode);
                throw new SubstrateUnavailableException("The native chess reader could not complete this read.");
            }
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Native chess reader transport failed on {Path}", path);
            if (TryGetExternalEndpoint() is null) StopChild();
            throw new SubstrateUnavailableException("The native chess reader is reconnecting. Retry the request.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Uri? TryGetExternalEndpoint()
    {
        var raw = Environment.GetEnvironmentVariable("LAPLACE_MODULAR_HTTP_ENDPOINT")
            ?? Environment.GetEnvironmentVariable("LAPLACE_NATIVE_READER_URL");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
            throw new SubstrateUnavailableException("LAPLACE_MODULAR_HTTP_ENDPOINT must be a loopback http URL.");
        return uri;
    }

    private static string? ReadExternalToken()
    {
        var direct = Environment.GetEnvironmentVariable("LAPLACE_MODULAR_MCP_TOKEN")
            ?? Environment.GetEnvironmentVariable("LAPLACE_MCP_TOKEN");
        if (!string.IsNullOrWhiteSpace(direct) && direct.Trim().Length >= 32)
            return direct.Trim();

        foreach (var candidate in new[]
        {
            Environment.GetEnvironmentVariable("LAPLACE_MODULAR_MCP_TOKEN_FILE"),
            "/etc/laplace/secrets/modular-mcp.env",
        })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
            try
            {
                foreach (var line in File.ReadLines(candidate))
                {
                    if (!line.StartsWith("LAPLACE_MCP_TOKEN=", StringComparison.Ordinal)) continue;
                    var value = line["LAPLACE_MCP_TOKEN=".Length..].Trim().Trim('"', '\'');
                    if (value.Length >= 32) return value;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (TryGetExternalEndpoint() is { } external)
        {
            if (_origin is not null) return;
            _token = ReadExternalToken()
                ?? throw new SubstrateUnavailableException("The external native reader token is unavailable.");
            _origin = external;
            return;
        }

        if (_process is { HasExited: false } && _origin is not null)
            return;

        StopChild();

        var lpmExe = ResolveLpmBinary()
            ?? throw new SubstrateUnavailableException("The installed native reader (lpm) is unavailable.");
        var releaseRoot = Path.GetDirectoryName(Path.GetDirectoryName(lpmExe));

        var tier0 = ResolveAsset("LAPLACE_TIER0", "tier0.bin", releaseRoot);
        var flags = ResolveAsset("LAPLACE_FLAGS", "tier0.flags", releaseRoot);
        var highway = ResolveAsset("LAPLACE_HIGHWAY", "highway.bin", releaseRoot, altName: "tier0.highway");
        var chess = ResolveAsset("LAPLACE_CHESS", "tier0.chess", releaseRoot);
        if (tier0 is null || flags is null || highway is null || chess is null)
        {
            var missing = new List<string>();
            if (tier0 is null) missing.Add("tier0");
            if (flags is null) missing.Add("flags");
            if (highway is null) missing.Add("highway");
            if (chess is null) missing.Add("chess");
            throw new SubstrateUnavailableException(
                $"The installed native reader or perf-cache is unavailable. ({string.Join(", ", missing)})");
        }

        var port = PickLoopbackPort();
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var connInfo = LaplaceEngineRuntime.ResolveLibpqConnInfo();

        var psi = new ProcessStartInfo(lpmExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.Environment["LAPLACE_MCP_TOKEN"] = secret;
        psi.Environment["LAPLACE_CONNINFO"] = connInfo;
        psi.Environment["LAPLACE_TIER0"] = tier0;
        psi.Environment["LAPLACE_FLAGS"] = flags;
        psi.Environment["LAPLACE_HIGHWAY"] = highway;
        psi.Environment["LAPLACE_CHESS"] = chess;

        if (ResolveDir("LAPLACE_MANIFEST", "manifest", releaseRoot) is { } manifest)
            psi.Environment["LAPLACE_MANIFEST"] = manifest;
        if (ResolveDir("LAPLACE_RECIPES", "recipes", releaseRoot) is { } recipes)
            psi.Environment["LAPLACE_RECIPES"] = recipes;
        if (ResolveDir("LAPLACE_FIRMWARE", "firmware", releaseRoot) is { } firmware)
            psi.Environment["LAPLACE_FIRMWARE"] = firmware;
        if (ResolveDir("LAPLACE_GRAMMARS", "grammars", releaseRoot) is { } grammars)
            psi.Environment["LAPLACE_GRAMMARS"] = grammars;

        var proc = Process.Start(psi)
            ?? throw new SubstrateUnavailableException("The installed native reader (lpm) could not be started.");
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogDebug("lpm: {Line}", e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var origin = new Uri($"http://127.0.0.1:{port}");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited)
            {
                var code = proc.ExitCode;
                proc.Dispose();
                throw new SubstrateUnavailableException($"The native chess reader exited during startup (code {code}).");
            }
            try
            {
                using var check = await _http.GetAsync(new Uri(origin, "/health"), ct).ConfigureAwait(false);
                if (check.IsSuccessStatusCode)
                {
                    _process = proc;
                    _origin = origin;
                    _token = secret;
                    return;
                }
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        try { proc.Kill(entireProcessTree: true); } catch { }
        proc.Dispose();
        throw new SubstrateUnavailableException("The native chess reader did not answer /health within 15 seconds.");
    }

    private static string? ResolveLpmBinary()
    {
        var exeName = OperatingSystem.IsWindows() ? "lpm.exe" : "lpm";
        var explicitExe = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_MCP_EXE");
        if (!string.IsNullOrWhiteSpace(explicitExe) && File.Exists(explicitExe.Trim()))
            return Path.GetFullPath(explicitExe.Trim());

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, exeName),
            Path.Combine(AppContext.BaseDirectory, "bin", exeName),
        };

        if (!OperatingSystem.IsWindows())
            candidates.Add("/hosting/laplace/modular/current/bin/lpm");

        if (LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX") is { Length: > 0 } prefix)
            candidates.Add(Path.Combine(prefix.Trim(), "bin", exeName));

        // Installed Windows modular client selection (installation.json -> <release>/bin/lpm.exe)
        if (LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_DATA_ROOT") is { Length: > 0 } dataRoot)
        {
            var installJson = Path.Combine(dataRoot.Trim(), "clients", "laplace", "installation.json");
            if (TryReadReleaseFromInstallation(installJson) is { } release)
                candidates.Add(Path.Combine(release, "bin", exeName));
        }

        if (LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_BUILD") is { Length: > 0 } build)
            candidates.Add(Path.Combine(build.Trim(), "Laplace-MCP", "icx-release", exeName));

        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }

    private static string? TryReadReleaseFromInstallation(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("release", out var rel) && rel.ValueKind == JsonValueKind.String)
                return rel.GetString();
        }
        catch { }
        return null;
    }

    private static string? ResolveAsset(string envVar, string primaryName, string? releaseRoot, string? altName = null)
    {
        if (LaplaceInstall.TryGetProcessOrMachineSetting(envVar) is { Length: > 0 } configured
            && File.Exists(configured.Trim()))
            return Path.GetFullPath(configured.Trim());

        var bases = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "share", "laplace"),
        };
        if (!string.IsNullOrWhiteSpace(releaseRoot))
        {
            bases.Add(Path.Combine(releaseRoot, "share", "prepared"));
            bases.Add(Path.Combine(releaseRoot, "share", "laplace"));
        }
        if (LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK") is { Length: > 0 } work)
            bases.Add(Path.Combine(work.Trim(), "tier0"));
        if (LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX") is { Length: > 0 } prefix)
            bases.Add(Path.Combine(prefix.Trim(), "share", "laplace"));

        foreach (var dir in bases)
        {
            var p1 = Path.Combine(dir, primaryName);
            if (File.Exists(p1)) return Path.GetFullPath(p1);
            if (altName is not null)
            {
                var p2 = Path.Combine(dir, altName);
                if (File.Exists(p2)) return Path.GetFullPath(p2);
            }
        }
        return null;
    }

    private static string? ResolveDir(string envVar, string subDir, string? releaseRoot)
    {
        if (LaplaceInstall.TryGetProcessOrMachineSetting(envVar) is { Length: > 0 } configured
            && Directory.Exists(configured.Trim()))
            return Path.GetFullPath(configured.Trim());

        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "share", "laplace", subDir),
            Path.Combine(AppContext.BaseDirectory, subDir),
            releaseRoot is not null ? Path.Combine(releaseRoot, "share", subDir) : null,
            releaseRoot is not null ? Path.Combine(releaseRoot, "share", "laplace", subDir) : null,
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static int PickLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void StopChild()
    {
        _origin = null;
        _token = "";
        if (_process is null) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose()
    {
        _disposed = true;
        StopChild();
        _http.Dispose();
        _gate.Dispose();
    }
}
