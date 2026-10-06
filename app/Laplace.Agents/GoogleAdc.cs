using System.Net.Http.Headers;
using System.Text.Json;

namespace Laplace.Agents;

/// <summary>
/// A minted Application Default Credentials session: the bearer to send, and the
/// project and location the publisher URL is built from. The access token is the
/// only secret; project and location are resource ids.
/// </summary>
internal readonly record struct AdcSession(string AccessToken, string ProjectId, string Location);

/// <summary>
/// Mints a Google Cloud access token from Application Default Credentials and
/// builds the Vertex AI publisher base URL those credentials bill.
///
/// This is the OAuth refresh behind <c>gcloud auth application-default login</c>,
/// not a request signature. The credential file holds a refresh token; each mint
/// exchanges it at the Google token endpoint. A service-account JSON is refused:
/// that file is a different grant, and this path is the user ADC the Cloud
/// credit is attached to.
///
/// <c>gemini-3.x</c> publisher models answer on location <c>global</c>. A regional
/// location returns 404 for those models, so the location defaults to
/// <c>global</c> unless <c>GOOGLE_CLOUD_LOCATION</c> (or the Cloud SDK region
/// variables) says otherwise. The global API host has no location prefix;
/// <c>global-aiplatform.googleapis.com</c> is not a host.
/// </summary>
internal static class GoogleAdc
{
    internal const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private static readonly object Gate = new();
    private static string? CachedToken;
    private static string? CachedKey;
    private static DateTimeOffset CachedUntil;

    /// <summary>True when a call through the vertex provider can mint a token and name a project.</summary>
    public static bool IsConfigured(Func<string, string?> env)
    {
        try
        {
            Read(env);
            return true;
        }
        catch (AgentException)
        {
            return false;
        }
    }

    /// <summary>The project a vertex call would bill, without minting a token.</summary>
    public static bool TryQuotaProject(Func<string, string?> env, out string project)
    {
        project = "";
        try
        {
            project = Read(env).ProjectId;
            return true;
        }
        catch (AgentException)
        {
            return false;
        }
    }

    /// <summary>The publisher base URL when credentials and a project both resolve; otherwise false.</summary>
    public static bool TryPublisherBaseUrl(Func<string, string?> env, out string url)
    {
        url = "";
        try
        {
            var material = Read(env);
            url = PublisherBaseUrl(material.ProjectId, material.Location);
            return true;
        }
        catch (AgentException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>{host}/v1/projects/{project}/locations/{location}/publishers/google</c>.
    /// The Google wire appends <c>/models/{model}:generateContent</c>.
    /// </summary>
    public static string PublisherBaseUrl(string project, string location)
    {
        var projectId = RequireId(project, "project");
        var locationId = RequireId(location, "location");
        var host = locationId.Equals("global", StringComparison.Ordinal)
            ? "https://aiplatform.googleapis.com"
            : $"https://{locationId}-aiplatform.googleapis.com";
        return $"{host}/v1/projects/{projectId}/locations/{locationId}/publishers/google";
    }

    /// <summary>
    /// Exchange the refresh token. <paramref name="handler"/> is the test seam;
    /// production passes null and caches the access token until shortly before
    /// <c>expires_in</c>. The cache key is the credentials file and its write
    /// time, so a new login replaces the token on the next call.
    /// </summary>
    public static AdcSession Mint(Func<string, string?> env, HttpMessageHandler? handler = null)
    {
        var material = Read(env);
        if (handler is null)
        {
            lock (Gate)
            {
                var key = CacheKey(material);
                if (CachedToken is not null && CachedKey == key && DateTimeOffset.UtcNow < CachedUntil)
                    return new AdcSession(CachedToken, material.ProjectId, material.Location);
            }
        }

        var (token, expiresIn) = Refresh(material, handler);
        if (handler is null)
        {
            lock (Gate)
            {
                CachedToken = token;
                CachedKey = CacheKey(material);
                CachedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresIn - 60));
            }
        }

        return new AdcSession(token, material.ProjectId, material.Location);
    }

    /// <summary>
    /// Adds <c>x-goog-user-project</c> unless the agent already set it. User ADC
    /// is rejected by Google without a quota project, and the header is how that
    /// project is named on the request.
    /// </summary>
    public static IReadOnlyDictionary<string, string> WithQuotaProject(
        IReadOnlyDictionary<string, string>? headers, string project)
    {
        var merged = headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        if (!merged.ContainsKey("x-goog-user-project"))
            merged["x-goog-user-project"] = project;
        return merged;
    }

    private static string CacheKey(Material material) =>
        material.Path + "|" + material.WriteUtc.Ticks;

    private readonly record struct Material(
        string Path,
        DateTime WriteUtc,
        string ClientId,
        string ClientSecret,
        string RefreshToken,
        string ProjectId,
        string Location);

    private static Material Read(Func<string, string?> env)
    {
        var path = CredentialsPath(env);
        if (!File.Exists(path))
            throw new AgentException(
                "no Application Default Credentials at " + path + ". Run " +
                "`gcloud auth application-default login`, then " +
                "`gcloud auth application-default set-quota-project PROJECT_ID`. " +
                "The vertex provider bills that Cloud project; a Generative Language API key does not.");

        JsonDocument doc;
        try
        {
            using var stream = File.OpenRead(path);
            doc = JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AgentException($"Application Default Credentials at {path} could not be read: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = StringProp(root, "type");
            if (!string.Equals(type, "authorized_user", StringComparison.Ordinal))
                throw new AgentException(
                    $"Application Default Credentials at {path} are type '{type ?? "missing"}'. " +
                    "The vertex provider mints type authorized_user " +
                    "(`gcloud auth application-default login`), not a service-account key.");

            var clientId = Required(root, "client_id", path);
            var clientSecret = Required(root, "client_secret", path);
            var refresh = Required(root, "refresh_token", path);
            var project = ResolveProject(env, StringProp(root, "quota_project_id"));
            var location = ResolveLocation(env);
            return new Material(path, File.GetLastWriteTimeUtc(path), clientId, clientSecret, refresh, project, location);
        }
    }

    private static string CredentialsPath(Func<string, string?> env)
    {
        var explicitPath = env("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath.Trim();
        return Path.Combine(Home(env), ".config", "gcloud", "application_default_credentials.json");
    }

    private static string Home(Func<string, string?> env)
    {
        foreach (var name in new[] { "HOME", "USERPROFILE" })
        {
            var value = env(name);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// Project: <c>GOOGLE_CLOUD_PROJECT</c>, then <c>GCLOUD_PROJECT</c>, then
    /// <c>GOOGLE_PROJECT</c>, then <c>quota_project_id</c> in the ADC file, then
    /// the active gcloud configuration's <c>[core] project</c>.
    /// </summary>
    private static string ResolveProject(Func<string, string?> env, string? quotaProject)
    {
        foreach (var name in new[] { "GOOGLE_CLOUD_PROJECT", "GCLOUD_PROJECT", "GOOGLE_PROJECT" })
        {
            var value = env(name);
            if (!string.IsNullOrWhiteSpace(value)) return RequireId(value.Trim(), "project");
        }

        if (!string.IsNullOrWhiteSpace(quotaProject))
            return RequireId(quotaProject.Trim(), "project");

        var fromConfig = ProjectFromGcloudConfig(Home(env));
        if (fromConfig is not null) return RequireId(fromConfig, "project");

        throw new AgentException(
            "Application Default Credentials have no Cloud project. Set GOOGLE_CLOUD_PROJECT, " +
            "or run `gcloud auth application-default set-quota-project PROJECT_ID`, " +
            "or `gcloud config set project PROJECT_ID`.");
    }

    private static string ResolveLocation(Func<string, string?> env)
    {
        foreach (var name in new[] { "GOOGLE_CLOUD_LOCATION", "CLOUDSDK_COMPUTE_REGION", "GOOGLE_CLOUD_REGION" })
        {
            var value = env(name);
            if (!string.IsNullOrWhiteSpace(value)) return RequireId(value.Trim(), "location");
        }

        return "global";
    }

    private static string? ProjectFromGcloudConfig(string home)
    {
        var directory = Path.Combine(home, ".config", "gcloud");
        var activeName = "default";
        var activePath = Path.Combine(directory, "active_config");
        if (File.Exists(activePath))
        {
            var read = File.ReadAllText(activePath).Trim();
            if (read.Length > 0 && read.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                activeName = read;
        }

        var configPath = Path.Combine(directory, "configurations", "config_" + activeName);
        if (!File.Exists(configPath)) return null;

        var inCore = false;
        foreach (var raw in File.ReadLines(configPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '!') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                inCore = line.Equals("[core]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inCore) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line[..eq].Trim().Equals("project", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(eq + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static (string Token, int ExpiresIn) Refresh(Material material, HttpMessageHandler? handler)
    {
        using var http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = material.ClientId,
                ["client_secret"] = material.ClientSecret,
                ["refresh_token"] = material.RefreshToken,
                ["grant_type"] = "refresh_token",
            }),
        };

        HttpResponseMessage response;
        string body;
        try
        {
            response = http.Send(request);
            body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new AgentException(
                "Application Default Credentials could not reach the Google token endpoint: " + ex.Message, ex);
        }

        using (response)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException ex)
            {
                throw new AgentException(
                    $"Google token endpoint returned a non-JSON body ({(int)response.StatusCode}).", ex);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!response.IsSuccessStatusCode)
                {
                    var reason = StringProp(root, "error_description") ?? StringProp(root, "error") ?? response.StatusCode.ToString();
                    throw new AgentException(
                        $"Application Default Credentials were refused by the Google token endpoint " +
                        $"({(int)response.StatusCode} {reason}). Run `gcloud auth application-default login` again.");
                }

                var token = StringProp(root, "access_token");
                if (string.IsNullOrWhiteSpace(token) || token.Contains('\n'))
                    throw new AgentException("Google token endpoint returned no access_token.");

                var expires = 3600;
                if (root.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsed))
                    expires = parsed;
                return (token, expires);
            }
        }
    }

    private static string Required(JsonElement root, string name, string path)
    {
        var value = StringProp(root, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new AgentException($"Application Default Credentials at {path} have no '{name}'.");
        return value;
    }

    private static string? StringProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            return null;
        var value = node.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string RequireId(string value, string what)
    {
        if (value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return value;
        throw new AgentException($"Cloud {what} '{value}' is not a single resource id.");
    }
}
