using System.Text.Json;
using System.Text;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>A previously verified provider binding explicitly authorized by the installation.</summary>
public sealed record PlatformOwner(string Provider, string Issuer, string Subject, string ClientId,
    DateTimeOffset VerifiedAt, string? DisplayName, string? Email);

/// <summary>Installation authority, separate from content identity and browser authentication.</summary>
public sealed class PlatformOwnerPolicy
{
    public static PlatformOwnerPolicy Empty { get; } = new([]);
    public IReadOnlyList<PlatformOwner> Owners { get; }

    private PlatformOwnerPolicy(PlatformOwner[] owners) => Owners = Array.AsReadOnly(owners);

    public static PlatformOwnerPolicy Load(Func<string, string?>? configuration = null)
    {
        configuration ??= Environment.GetEnvironmentVariable;
        try
        {
            var json = configuration("LAPLACE_PLATFORM_OWNER_POLICY");
            var file = configuration("LAPLACE_PLATFORM_OWNER_POLICY_FILE");
            if (!string.IsNullOrWhiteSpace(file))
            {
                var fileJson = ReadPolicy(file);
                var policy = Parse(fileJson, configuration);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    _ = Parse(json, configuration);
                    using var inlineDocument = JsonDocument.Parse(json);
                    using var fileDocument = JsonDocument.Parse(fileJson);
                    if (!JsonElement.DeepEquals(inlineDocument.RootElement, fileDocument.RootElement)) throw Invalid();
                }
                return policy;
            }
            return string.IsNullOrWhiteSpace(json) ? Empty : Parse(json, configuration);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Do not include the policy, provider identifiers or parser excerpts in logs.
            throw Invalid();
        }
    }

    private static string ReadPolicy(string file)
    {
        if (!Path.IsPathFullyQualified(file)) throw Invalid();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[65537];
        var used = 0;
        int read;
        while (used < bytes.Length && (read = stream.Read(bytes, used, bytes.Length - used)) > 0) used += read;
        if (used > 65536) throw Invalid();
        return new UTF8Encoding(false, true).GetString(bytes, 0, used);
    }

    private static PlatformOwnerPolicy Parse(string json, Func<string, string?> configuration)
    {
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw Invalid();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Fields(root, "version", "owners");
        if (!root.GetProperty("version").TryGetInt32(out var version) || version != 1) throw Invalid();
        var entries = root.GetProperty("owners");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 16) throw Invalid();
        var owners = new List<PlatformOwner>();
        var bindings = new HashSet<(string, string, string)>();
        foreach (var entry in entries.EnumerateArray())
        {
            Fields(entry, "provider", "issuer", "subject", "client_id", "verified_at", "display_name", "email");
            var provider = Required(entry, "provider", 32);
            if (provider is not ("google" or "microsoft")) throw Invalid();
            var issuer = Required(entry, "issuer", 2048);
            if (provider == "google" && issuer is not ("https://accounts.google.com" or "accounts.google.com")) throw Invalid();
            if (provider == "microsoft" && !MicrosoftIssuer(issuer)) throw Invalid();
            var subject = Required(entry, "subject", 1024);
            var client = Required(entry, "client_id", 1024);
            var configuredClient = configuration($"LAPLACE_AUTH_{provider.ToUpperInvariant()}_CLIENT_ID");
            if (!string.Equals(client, configuredClient, StringComparison.Ordinal)) throw Invalid();
            var timestamp = Required(entry, "verified_at", 64);
            if (!System.Text.RegularExpressions.Regex.IsMatch(timestamp, @"(?:Z|[+-]\d{2}:\d{2})$")
                || !entry.GetProperty("verified_at").TryGetDateTimeOffset(out var verified)
                || verified <= DateTimeOffset.UnixEpoch || verified > DateTimeOffset.UtcNow) throw Invalid();
            if (!bindings.Add((provider, issuer, subject))) throw Invalid();
            owners.Add(new(provider, issuer, subject, client, verified.ToUniversalTime(),
                Optional(entry, "display_name", 512), Optional(entry, "email", 320)));
        }
        return new(owners.ToArray());
    }

    public bool Matches(string provider, string issuer, string subject) => Owners.Any(owner =>
        owner.Provider == provider && owner.Issuer == issuer && owner.Subject == subject);

    private static bool MicrosoftIssuer(string issuer)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.Host != "login.microsoftonline.com" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "D", out _) && parts[1] == "v2.0";
    }

    private static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw Invalid();
    }

    private static string Required(JsonElement value, string field, int maximum)
    {
        var text = value.GetProperty(field).GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text != text.Trim() || text.Any(char.IsControl)) throw Invalid();
        return text;
    }

    private static string? Optional(JsonElement value, string field, int maximum)
    {
        if (!value.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        return Required(value, field, maximum);
    }

    private static InvalidOperationException Invalid() => new(
        "Invalid installation owner policy or OAuth client mismatch. No owner reconciliation was performed.");
}
