using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Laplace.Agents;

/// <summary>
/// A minted Application Default Credentials session: the bearer to send, and the
/// project and location the publisher URL is built from. The access token is the
/// only secret; project and location are resource ids.
/// </summary>
internal readonly record struct AdcSession(string AccessToken, string ProjectId, string Location, string? QuotaProject);

/// <summary>
/// Resolves Vertex resource and quota projects separately. Google's auth library
/// loads ADC, exchanges and refreshes tokens, and supports its credential types.
/// Descriptor probes inspect local configuration without refreshing credentials.
/// </summary>
internal static class GoogleAdc
{
    internal const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private static readonly object Gate = new();
    private static GoogleCredential? CachedCredential;
    private static string? CachedKey;

    /// <summary>True when a call through the vertex provider can mint a token and name a project.</summary>
    public static bool IsConfigured(Func<string, string?> env)
    {
        try
        {
            return Read(env).Path.Length > 0;
        }
        catch (AgentException)
        {
            return false;
        }
    }

    /// <summary>The configured quota project, without minting a token.</summary>
    public static bool TryQuotaProject(Func<string, string?> env, out string project)
    {
        project = "";
        try
        {
            project = Read(env).QuotaProject ?? "";
            return project.Length > 0;
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

    /// <summary>Mint with the SDK's expiry-aware token cache. A file-content
    /// fingerprint invalidates credentials even when a replacement preserves mtime.</summary>
    public static AdcSession Mint(Func<string, string?> env, HttpMessageHandler? handler = null)
    {
        var material = Read(env);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            GoogleCredential credential;
            if (handler is null)
            {
                lock (Gate)
                {
                    var key = material.Path + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.Json)))
                        + "|" + material.QuotaProject;
                    if (CachedCredential is null || CachedKey != key)
                    {
                        CachedCredential = LoadCredential(material, timeout.Token);
                        CachedKey = key;
                    }
                    credential = CachedCredential;
                }
            }
            else
            {
                credential = LoadCredential(material, timeout.Token).CreateWithHttpClientFactory(
                    new HttpClientFromMessageHandlerFactory(_ => new(handler, false, false)));
            }

            var token = ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: timeout.Token)
                .GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n'))
                throw new AgentException("Google ADC returned no usable access token.");
            return new AdcSession(token, material.ProjectId, material.Location, credential.QuotaProject);
        }
        catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex)
        {
            // The SDK exception may contain response text. Return status only;
            // neither credentials nor an echoed token enter the model's context.
            var status = ex.StatusCode is { } code ? ((int)code).ToString() : "unknown status";
            throw new AgentException($"Google ADC token exchange was refused ({status}). " +
                "Check the configured identity or run gcloud auth application-default login again.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException
            or ArgumentException or InvalidOperationException or CryptographicException)
        {
            throw new AgentException($"Google ADC ({material.CredentialType}) could not load or refresh " +
                $"credentials ({ex.GetType().Name}). Check the configured credential file and identity.");
        }
    }

    private static GoogleCredential LoadCredential(Material material, CancellationToken cancellationToken)
    {
        var credential = material.Path.Length == 0
            ? GoogleCredential.GetApplicationDefaultAsync(cancellationToken).GetAwaiter().GetResult()
            : CredentialFactory.FromJson(material.Json, material.CredentialType);
        credential = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        return material.QuotaProject is null ? credential : credential.CreateWithQuotaProject(material.QuotaProject);
    }

    /// <summary>
    /// Adds the configured quota project as <c>x-goog-user-project</c> unless
    /// the agent already set the header.
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

    private readonly record struct Material(
        string Path, string Json, string CredentialType, string ProjectId, string Location, string? QuotaProject);

    private static Material Read(Func<string, string?> env)
    {
        var path = CredentialsPath(env);
        if (!File.Exists(path))
        {
            // On a declared Cloud resource, let the SDK discover the attached
            // identity. Probing descriptors still makes no metadata request.
            if (string.IsNullOrWhiteSpace(env("GOOGLE_APPLICATION_CREDENTIALS")) &&
                new[] { "GOOGLE_CLOUD_PROJECT", "GCLOUD_PROJECT", "GOOGLE_PROJECT" }
                    .Any(name => !string.IsNullOrWhiteSpace(env(name))))
            {
                var quota = env("GOOGLE_CLOUD_QUOTA_PROJECT");
                quota = string.IsNullOrWhiteSpace(quota) ? null : RequireId(quota.Trim(), "quota project");
                return new Material("", "", "application_default", ResolveProject(env, null), ResolveLocation(env), quota);
            }
            throw new AgentException("No Application Default Credentials at " + path + ". Run " +
                "gcloud auth application-default login and gcloud auth application-default set-quota-project PROJECT_ID, " +
                "or configure the workload identity and GOOGLE_CLOUD_PROJECT.");
        }

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = StringProp(root, "type") ?? throw new AgentException("Google ADC file has no credential type.");
            var quota = env("GOOGLE_CLOUD_QUOTA_PROJECT") ?? StringProp(root, "quota_project_id");
            if (!string.IsNullOrWhiteSpace(quota)) quota = RequireId(quota.Trim(), "quota project");
            else quota = null;
            var project = ResolveProject(env, StringProp(root, "project_id") ?? StringProp(root, "quota_project_id"));
            return new Material(path, json, type, project, ResolveLocation(env), quota);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AgentException($"Google ADC file at {path} could not be read ({ex.GetType().Name}).");
        }
    }

    private static string CredentialsPath(Func<string, string?> env)
    {
        var explicitPath = env("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath.Trim();
        var sdkConfig = env("CLOUDSDK_CONFIG");
        if (!string.IsNullOrWhiteSpace(sdkConfig)) return Path.Combine(sdkConfig.Trim(), "application_default_credentials.json");
        var appData = env("APPDATA");
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(appData))
            return Path.Combine(appData.Trim(), "gcloud", "application_default_credentials.json");
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

        var fromConfig = ProjectFromGcloudConfig(Path.GetDirectoryName(CredentialsPath(env))!);
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

    private static string? ProjectFromGcloudConfig(string directory)
    {
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

    private static string? StringProp(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
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
