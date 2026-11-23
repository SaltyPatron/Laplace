using System.Text;
using System.Text.Json.Serialization;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

public sealed record FirmwareDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("directives")] IReadOnlyDictionary<string, string> Directives,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("available_on_disk")] bool AvailableOnDisk);

public sealed record FirmwareListResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<FirmwareDescriptor> Data);

/// <summary>
/// Discovers detached <c>*.firmware</c> decision files across configured installation/source
/// directories, computes their BLAKE3-128 identifiers, parses key directives, and merges them
/// with built-in canonical profile metadata.
/// </summary>
internal static class FirmwareCatalog
{
    private sealed record CanonicalMeta(string Id, string Label, string Summary);

    private static readonly CanonicalMeta[] CanonicalProfiles =
    [
        new("program", "program (default)", "Balanced general OODA forward pass"),
        new("coding", "coding", "AST / symbol / toolchain-verified code generation"),
        new("deductive", "deductive", "High-trust deductive chain & formal closure"),
        new("inductive", "inductive", "Cross-witness empirical convergence & generalization"),
        new("abductive", "abductive", "Causal root-cause & best-explanation hypothesis"),
        new("constructive", "constructive", "Compositional synthesis across multi-tier entities"),
        new("adversarial", "adversarial", "Counter-example probing & contradiction audit"),
        new("chess", "chess", "Variant laws, Chess960/Syzygy rules, and search policy"),
        new("without-layout", "without-layout", "Layout-free navigation refusing SpaceAfter and punctuation"),
    ];

    private static readonly HashSet<string> ScalarDirectives = new(StringComparer.Ordinal)
    {
        "k", "lambda", "hops", "fan", "emit", "enough", "ooda", "salience", "risk",
        "abstain", "fact", "top", "order", "tie", "seed", "shape", "rounds",
        "breadth", "nearest", "walks", "walkfan", "steps", "restart", "sure",
        "lift", "multitier", "combine", "default", "search", "elect", "prevalence",
    };

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static IReadOnlyList<FirmwareDescriptor> List()
    {
        var discovered = ScanDiskFirmwares();
        var canonicalById = CanonicalProfiles.ToDictionary(static m => m.Id, StringComparer.OrdinalIgnoreCase);
        var entries = new List<FirmwareDescriptor>(CanonicalProfiles.Length + discovered.Count);

        foreach (var meta in CanonicalProfiles)
        {
            if (discovered.TryGetValue(meta.Id, out var file))
                entries.Add(ParseFile(meta.Id, meta.Label, meta.Summary, file));
            else
                entries.Add(EmptyDescriptor(meta.Id, meta.Label, meta.Summary));
        }

        foreach (var pair in discovered.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (canonicalById.ContainsKey(pair.Key))
                continue;
            entries.Add(ParseFile(pair.Key, pair.Key, summaryOverride: null, pair.Value));
        }

        return entries;
    }

    public static bool TryResolve(string? name, out FirmwareDescriptor entry)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            entry = default!;
            return false;
        }

        string trimmed = name.Trim();
        if (trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            entry = default!;
            return false;
        }

        string normalized = trimmed.EndsWith(".firmware", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^".firmware".Length]
            : trimmed;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            entry = default!;
            return false;
        }

        foreach (var candidate in List())
        {
            if (string.Equals(candidate.Id, normalized, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                return true;
            }
            if (trimmed.Length == 32
                && !string.IsNullOrEmpty(candidate.IdHex)
                && string.Equals(candidate.IdHex, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                return true;
            }
        }

        entry = default!;
        return false;
    }

    private static Dictionary<string, string> ScanDiskFirmwares()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitFile = ReadSetting("LAPLACE_FIRMWARE");
        if (!string.IsNullOrWhiteSpace(explicitFile) && File.Exists(explicitFile))
        {
            string full = Path.GetFullPath(explicitFile);
            if (string.Equals(Path.GetExtension(full), ".firmware", StringComparison.OrdinalIgnoreCase))
            {
                string stem = Path.GetFileNameWithoutExtension(full);
                if (!string.IsNullOrWhiteSpace(stem))
                    found[stem] = full;
            }
        }

        foreach (var dir in CandidateDirectories())
        {
            if (!Directory.Exists(dir))
                continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.firmware", SearchOption.TopDirectoryOnly)
                             .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrWhiteSpace(stem) && !found.ContainsKey(stem))
                        found[stem] = Path.GetFullPath(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable firmware directories; fall back to remaining locations or built-in metadata.
            }
        }

        return found;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var seen = new HashSet<string>(PathComparer);
        foreach (var raw in RawCandidateDirectories())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            string full;
            try
            {
                full = Path.GetFullPath(raw.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (seen.Add(full))
                yield return full;
        }
    }

    private static IEnumerable<string?> RawCandidateDirectories()
    {
        var fwSetting = ReadSetting("LAPLACE_FIRMWARE");
        if (!string.IsNullOrWhiteSpace(fwSetting))
        {
            if (Directory.Exists(fwSetting))
                yield return fwSetting;
            else
                yield return Path.GetDirectoryName(fwSetting);
        }

        var chessFw = ReadSetting("LAPLACE_CHESS_FIRMWARE");
        if (!string.IsNullOrWhiteSpace(chessFw))
            yield return Directory.Exists(chessFw) ? chessFw : Path.GetDirectoryName(chessFw);

        var prefix = ReadSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
            yield return Path.Combine(prefix, "share", "laplace", "firmware");

        yield return Path.Combine(AppContext.BaseDirectory, "share", "laplace", "firmware");
        yield return Path.Combine(AppContext.BaseDirectory, "firmware");

        var enginePkg = ReadSetting("LAPLACE_ENGINE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(enginePkg))
            yield return Path.Combine(enginePkg, "share", "laplace", "firmware");

        var recipes = ReadSetting("LAPLACE_RECIPES");
        if (!string.IsNullOrWhiteSpace(recipes))
        {
            var recipesParent = Path.GetDirectoryName(recipes.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(recipesParent))
                yield return Path.Combine(recipesParent, "firmware");
        }

        var src = ReadSetting("LAPLACE_SRC");
        if (!string.IsNullOrWhiteSpace(src))
            yield return Path.Combine(src, "Laplace-Engine", "firmware");

        if (LaplaceInstall.TryRepoRoot(out var repoRoot))
        {
            yield return Path.Combine(repoRoot, "firmware");
            var workspaceRoot = Path.GetDirectoryName(repoRoot);
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
                yield return Path.Combine(workspaceRoot, "Laplace-Engine", "firmware");
        }

        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var sibling = Path.Combine(dir.FullName, "Laplace-Engine", "firmware");
            if (Directory.Exists(sibling))
            {
                yield return sibling;
                break;
            }
        }
    }

    private static string? ReadSetting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } envVal
            ? envVal.Trim()
            : LaplaceInstall.TryGetProcessOrMachineSetting(name);

    private static FirmwareDescriptor EmptyDescriptor(string id, string label, string summary) =>
        new(
            Id: id,
            Label: label,
            Summary: summary,
            Path: null,
            IdHex: string.Empty,
            Directives: new Dictionary<string, string>(StringComparer.Ordinal),
            Bytes: 0,
            AvailableOnDisk: false);

    private static FirmwareDescriptor ParseFile(string id, string label, string? summaryOverride, string filePath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EmptyDescriptor(id, label, summaryOverride ?? $"Detached firmware '{id}' (unreadable on disk)");
        }

        string idHex = Hash128.Blake3(bytes).ToHex();
        string text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var (headerSummary, directives) = ParseContent(text);
        string summary = !string.IsNullOrWhiteSpace(summaryOverride)
            ? summaryOverride
            : !string.IsNullOrWhiteSpace(headerSummary)
                ? headerSummary
                : $"Detached firmware profile '{id}'";

        return new FirmwareDescriptor(
            Id: id,
            Label: label,
            Summary: summary,
            Path: filePath,
            IdHex: idHex,
            Directives: directives,
            Bytes: bytes.LongLength,
            AvailableOnDisk: true);
    }

    private static (string? HeaderSummary, IReadOnlyDictionary<string, string> Directives) ParseContent(string text)
    {
        var directives = new Dictionary<string, string>(StringComparer.Ordinal);
        string? headerSummary = null;
        bool inHeaderComments = true;
        string section = string.Empty;
        var variants = new List<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (headerSummary is not null)
                    inHeaderComments = false;
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (inHeaderComments && headerSummary is null)
                {
                    var comment = line.TrimStart('#', ' ', '\t');
                    if (comment.Length > 0)
                        headerSummary = comment;
                }
                continue;
            }

            inHeaderComments = false;
            var cleaned = StripInlineComment(line);
            if (cleaned.Length == 0)
                continue;

            var (keyword, rest) = SplitFirstToken(cleaned);
            if (keyword.Length == 0)
                continue;

            if (string.Equals(keyword, "for", StringComparison.Ordinal))
            {
                var (op, _) = SplitFirstToken(rest);
                section = op;
                continue;
            }

            if (string.Equals(keyword, "variant", StringComparison.Ordinal))
            {
                var variantName = ReadPossiblyQuotedToken(rest);
                if (variantName.Length > 0)
                    variants.Add(variantName);
                continue;
            }

            if (string.Equals(keyword, "refuse", StringComparison.Ordinal)
                || string.Equals(keyword, "only", StringComparison.Ordinal))
            {
                var (subKind, subValues) = SplitFirstToken(rest);
                if (subKind.Length > 0 && subValues.Length > 0)
                    directives[$"{keyword}.{subKind}"] = Truncate(subValues, 120);
                continue;
            }

            if (!ScalarDirectives.Contains(keyword) || rest.Length == 0)
                continue;

            if (string.Equals(keyword, "fan", StringComparison.Ordinal) && section is "hop" or "search")
            {
                directives[$"{section}.fan"] = rest;
                if (!directives.ContainsKey("fan"))
                    directives["fan"] = rest;
                continue;
            }

            if (section.Length == 0 || string.Equals(section, "pull", StringComparison.Ordinal) || !directives.ContainsKey(keyword))
                directives[keyword] = Truncate(rest, 120);
        }

        if (variants.Count > 0)
            directives["variants"] = Truncate(string.Join(", ", variants), 120);

        return (headerSummary, directives);
    }

    private static string StripInlineComment(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
                inQuotes = !inQuotes;
            else if (ch == '#' && !inQuotes)
                return line[..i].Trim();
        }
        return line.Trim();
    }

    private static (string Head, string Tail) SplitFirstToken(string input)
    {
        int i = 0;
        while (i < input.Length && !char.IsWhiteSpace(input[i]))
            i++;
        string head = input[..i];
        string rest = i < input.Length ? input[i..].Trim() : string.Empty;
        return (head, rest);
    }

    private static string ReadPossiblyQuotedToken(string input)
    {
        if (input.Length == 0)
            return string.Empty;
        if (input[0] == '"')
        {
            int end = input.IndexOf('"', 1);
            return end > 1 ? input[1..end] : input[1..].Trim();
        }
        return SplitFirstToken(input).Head;
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..(maxLen - 1)] + "…";
}
