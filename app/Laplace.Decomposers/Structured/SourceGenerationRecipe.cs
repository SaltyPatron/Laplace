using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Structured;

/// <summary>Execution configuration around the existing artifact disposition contract.</summary>
public sealed record SourceGenerationArtifactRule(
    SourceRecipeArtifact Artifact,
    string? RecipePath,
    int RecordDepth,
    JsonElement Configuration);

/// <summary>
/// A portable selected source generation. Provider configuration chooses native
/// algorithms; the existing artifact graph and shared writer retain admission authority.
/// </summary>
public class SourceGenerationRecipe
{
    public string ManifestPath { get; }
    public string Authority { get; }
    public string Release { get; }
    public string SourceName { get; }
    public Hash128 SourceId { get; }
    public double Trust { get; }
    public string TrustClass { get; }
    public int LayerOrder { get; }
    public IReadOnlyList<string> Aliases { get; }
    public string? Root { get; }
    public bool Selected { get; }
    public IReadOnlyList<SourceGenerationArtifactRule> Rules { get; }
    public string CanonicalForm { get; }

    protected SourceGenerationRecipe(string manifestPath, string authority, string release,
        string sourceName, Hash128 sourceId, double trust, string trustClass, int layerOrder,
        IReadOnlyList<SourceGenerationArtifactRule> rules, IReadOnlyList<string>? aliases = null,
        string? root = null, bool selected = false)
    {
        ManifestPath = manifestPath;
        Authority = authority;
        Release = release;
        SourceName = sourceName;
        SourceId = sourceId;
        Trust = trust;
        TrustClass = trustClass;
        LayerOrder = layerOrder;
        Aliases = aliases ?? [];
        Root = root;
        Selected = selected;
        Rules = rules;
        JsonElement configuration = JsonSerializer.SerializeToElement(new
        {
            authority, release, sourceName, sourceId = Convert.ToHexStringLower(sourceId.ToBytes()),
            trust, trustClass, layerOrder,
            artifacts = rules.OrderBy(static r => r.Artifact.Selector, StringComparer.Ordinal).Select(r => new
            {
                selector = r.Artifact.Selector, provider = r.Artifact.Provider, syntax = r.Artifact.Syntax,
                disposition = r.Artifact.Disposition.ToString(), required = r.Artifact.Required,
                reason = r.Artifact.Reason, dependsOn = (r.Artifact.DependsOn ?? []).Order(StringComparer.Ordinal),
                recipePath = r.RecipePath, recordDepth = r.RecordDepth, configuration = r.Configuration,
            }),
        });
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) WriteCanonical(writer, configuration);
        CanonicalForm = Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static SourceGenerationRecipe Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string manifest = Path.GetFullPath(path);
        using var stream = File.OpenRead(manifest);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        CheckProperties(root, ["authority", "release", "sourceName", "sourceId", "trustClass", "layerOrder", "artifacts", "aliases", "root", "selected"]);
        string authority = Required(root, "authority"), release = Required(root, "release");
        string sourceName = Required(root, "sourceName");
        string? sourceHex = Optional(root, "sourceId");
        // The source is the witness of its observations: [authority, release] as content.
        Hash128 sourceId = sourceHex is null ? SourceWitness.Id(authority, release)
            : ParseId(sourceHex);
        // How trustworthy the witness is: a governed trust class (a standards body above an
        // academic curation above a user-curated wiki above subtitles), whose prior seeds
        // the standing of every claim it makes. The class is the only statement of trust.
        string trustClass = Required(root, "trustClass");
        double trust = Laplace.Decomposers.Abstractions.SourceTrust.ForClass(
            SubstrateCanonicalIds.TrustClass(trustClass));
        int layer = root.TryGetProperty("layerOrder", out var layerValue) ? layerValue.GetInt32() : 0;
        if (layer is < 0 or > Laplace.Ingestion.LayerCompletion.MaxMarkedLayer)
            throw new InvalidDataException("Source generation layer is outside the supported completion range.");
        string[] aliases = root.TryGetProperty("aliases", out var aliasValues)
            ? aliasValues.EnumerateArray().Select(static item => item.GetString() ?? "").ToArray() : [];
        if (aliases.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Source-generation aliases cannot be empty.");
        string? sourceRoot = Optional(root, "root");
        if (sourceRoot is not null)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot)) throw new InvalidDataException("Source root cannot be empty.");
            sourceRoot = Path.GetFullPath(sourceRoot, Path.GetDirectoryName(manifest)!);
        }
        bool selected = root.TryGetProperty("selected", out var selectedValue) && selectedValue.GetBoolean();
        var rules = new List<SourceGenerationArtifactRule>();
        var selectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in root.GetProperty("artifacts").EnumerateArray())
        {
            CheckProperties(item, ["selector", "disposition", "reason", "provider", "syntax", "required", "dependsOn", "recipePath", "recordDepth", "configuration"]);
            string selector = ValidateRelative(Required(item, "selector"), allowGlob: true);
            if (!selectors.Add(selector)) throw new InvalidDataException($"Duplicate artifact selector '{selector}'.");
            string dispositionText = Required(item, "disposition");
            SourceArtifactDisposition disposition = dispositionText switch
            {
                "admitted" => SourceArtifactDisposition.Admitted,
                "equivalent-packaging" => SourceArtifactDisposition.EquivalentPackaging,
                "superseded" => SourceArtifactDisposition.Superseded,
                "excluded-with-reason" => SourceArtifactDisposition.Excluded,
                "unsupported-with-why-not" => SourceArtifactDisposition.Unsupported,
                "absent" => SourceArtifactDisposition.Absent,
                _ => Enum.TryParse<SourceArtifactDisposition>(dispositionText, false, out var parsed)
                    && Enum.IsDefined(parsed) ? parsed : throw new InvalidDataException($"Unknown artifact disposition '{dispositionText}'."),
            };
            string? reason = Optional(item, "reason");
            if (disposition != SourceArtifactDisposition.Admitted && string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException($"Artifact '{selector}' needs an explicit disposition reason.");
            string provider = Optional(item, "provider") ?? "";
            if (disposition == SourceArtifactDisposition.Admitted && string.IsNullOrWhiteSpace(provider))
                throw new InvalidDataException($"Admitted artifact '{selector}' needs a provider.");
            string? recipePath = Optional(item, "recipePath");
            if (recipePath is not null) recipePath = ValidateRelative(recipePath, allowGlob: false);
            int depth = item.TryGetProperty("recordDepth", out var depthValue) ? depthValue.GetInt32() : 2;
            if (depth is < 0 or > 128) throw new InvalidDataException($"Invalid record depth for '{selector}'.");
            string[] dependencies = item.TryGetProperty("dependsOn", out var deps)
                ? deps.EnumerateArray().Select(d => ValidateRelative(d.GetString() ?? "", true)).Distinct(StringComparer.Ordinal).ToArray()
                : [];
            JsonElement config = item.TryGetProperty("configuration", out var value)
                ? value.Clone() : JsonSerializer.SerializeToElement(new { });
            rules.Add(new(new SourceRecipeArtifact(selector, provider, Optional(item, "syntax") ?? "",
                disposition, item.TryGetProperty("required", out var required) && required.GetBoolean(),
                reason, dependencies), recipePath, depth, config));
        }
        if (rules.Count == 0) throw new InvalidDataException("A source generation needs artifact rules.");
        foreach (var rule in rules)
            foreach (string dependency in rule.Artifact.DependsOn ?? [])
                if (!selectors.Contains(dependency)) throw new InvalidDataException($"Unknown dependency selector '{dependency}'.");
        return new SourceGenerationRecipe(manifest, authority, release, sourceName, sourceId,
            trust, trustClass, layer, rules.AsReadOnly(), aliases, sourceRoot, selected);
    }

    internal static string ValidateRelative(string value, bool allowGlob)
    {
        string normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized)
            || normalized.Contains(':') || normalized.Contains('\0')
            || normalized.Split('/').Any(static part => part is "" or "." or "..")
            || normalized.IndexOfAny(['[', ']']) >= 0
            || (!allowGlob && normalized.IndexOfAny(['*', '?']) >= 0))
            throw new InvalidDataException($"Invalid relative source path or selector '{value}'.");
        return normalized;
    }

    private static Hash128 ParseId(string value)
    {
        byte[] bytes = Convert.FromHexString(value);
        if (bytes.Length != 16) throw new InvalidDataException("Source id needs 32 hexadecimal characters.");
        return Hash128.FromBytes(bytes);
    }

    private static string Required(JsonElement element, string name) =>
        Optional(element, name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException($"Source generation requires '{name}'.");
    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static void CheckProperties(JsonElement element, string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException($"Unknown or duplicate source-generation property '{property.Name}'.");
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
