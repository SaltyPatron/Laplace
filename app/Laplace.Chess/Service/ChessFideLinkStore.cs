using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

public sealed record ChessFideLinkWriteResult(
    string FideId,
    string Provider,
    string Username,
    string RootDirectory,
    string FilePath,
    bool Changed);

public sealed class ChessFideLinkEntry
{
    public string FideId { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChessCom { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lichess { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alias { get; set; }
}

public sealed class ChessFideLinkDocument
{
    public List<ChessFideLinkEntry> Links { get; set; } = [];
}

/// <summary>
/// Thread-safe, atomic writer for curated FIDE-to-provider player identity links at
/// <c>$LAPLACE_DATA/Games/Chess/fide/links/player-links.json</c>.
/// Curated links are strictly an admin corpus operation witnessed by
/// <c>Laplace Curated Chess</c> at <c>AcademicCurated</c>.
/// </summary>
public static class ChessFideLinkStore
{
    public const string FileName = "player-links.json";
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private static readonly JsonSerializerOptions WriteJson = new()
    {
        WriteIndented = true,
    };

    public static string ResolveLinksDirectory(string? dataRootOverride = null)
    {
        var dataRoot = !string.IsNullOrWhiteSpace(dataRootOverride)
            ? dataRootOverride.Trim()
            : ChessRuntimeConfiguration.Read("LAPLACE_DATA") is { Length: > 0 } configured
                ? configured.Trim()
                : LaplaceInstall.DeclaredIngestRoot();
        return Path.GetFullPath(Path.Combine(dataRoot, "Games", "Chess", "fide", "links"));
    }

    public static string ValidateFideId(string? fideId)
    {
        var trimmed = (fideId ?? "").Trim();
        if (trimmed.Length is < 4 or > 12 || !trimmed.All(char.IsAsciiDigit))
            throw new ArgumentException("fide_id must be 4 to 12 ASCII digits.", nameof(fideId));
        return trimmed;
    }

    public static string ValidateProvider(string? provider, bool allowAliases = false)
    {
        var normalized = (provider ?? "").Trim().ToLowerInvariant();
        if (allowAliases && normalized is "chess.com" or "chess")
            normalized = "chesscom";
        return normalized switch
        {
            "chesscom" or "lichess" or "fide" => normalized,
            _ => throw new ArgumentException("provider must be one of 'chesscom', 'lichess', or 'fide'.", nameof(provider)),
        };
    }

    public static string ValidateUsername(string? username)
    {
        var trimmed = (username ?? "").Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("username is required.", nameof(username));
        if (trimmed.Length > 256)
            throw new ArgumentException("username must be at most 256 characters.", nameof(username));
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("username must not contain control characters.", nameof(username));
        return trimmed;
    }

    public static async Task<ChessFideLinkWriteResult> UpsertAsync(
        string? fideId,
        string? provider,
        string? username,
        string? linksDirectoryOverride = null,
        bool allowProviderAliases = false,
        CancellationToken ct = default)
    {
        string cleanFideId = ValidateFideId(fideId);
        string cleanProvider = ValidateProvider(provider, allowProviderAliases);
        string cleanUsername = ValidateUsername(username);

        string rootDir = !string.IsNullOrWhiteSpace(linksDirectoryOverride)
            ? Path.GetFullPath(linksDirectoryOverride.Trim())
            : ResolveLinksDirectory();
        string filePath = Path.Combine(rootDir, FileName);

        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(rootDir);
            ChessFideLinkDocument doc = await ReadDocumentAsync(filePath, ct).ConfigureAwait(false);
            bool changed = UpsertEntry(doc.Links, cleanFideId, cleanProvider, cleanUsername);

            if (changed || !File.Exists(filePath))
            {
                string serialized = JsonSerializer.Serialize(doc, WriteJson) + "\n";
                string tempPath = Path.Combine(rootDir, $"{FileName}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllTextAsync(tempPath, serialized, Utf8NoBom, ct).ConfigureAwait(false);
                    File.Move(tempPath, filePath, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    throw;
                }
            }

            return new ChessFideLinkWriteResult(
                cleanFideId,
                cleanProvider,
                cleanUsername,
                rootDir,
                filePath,
                changed);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static async Task<ChessFideLinkDocument> ReadDocumentAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return new ChessFideLinkDocument();

        string raw = await File.ReadAllTextAsync(filePath, Utf8NoBom, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return new ChessFideLinkDocument();

        var parsed = JsonSerializer.Deserialize<ChessFideLinkDocument>(raw, ReadJson);
        if (parsed?.Links is null)
            return new ChessFideLinkDocument();

        // Normalize existing entries cleanly without dropping valid data.
        var clean = new List<ChessFideLinkEntry>(parsed.Links.Count);
        foreach (var entry in parsed.Links)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.FideId))
                continue;
            clean.Add(new ChessFideLinkEntry
            {
                FideId = entry.FideId.Trim(),
                ChessCom = string.IsNullOrWhiteSpace(entry.ChessCom) ? null : entry.ChessCom.Trim(),
                Lichess = string.IsNullOrWhiteSpace(entry.Lichess) ? null : entry.Lichess.Trim(),
                Alias = string.IsNullOrWhiteSpace(entry.Alias) ? null : entry.Alias.Trim(),
            });
        }
        return new ChessFideLinkDocument { Links = clean };
    }

    private static bool UpsertEntry(List<ChessFideLinkEntry> links, string fideId, string provider, string username)
    {
        // Idempotent check: exact (FideId, provider, username) already present.
        foreach (var entry in links)
        {
            if (!string.Equals(entry.FideId, fideId, StringComparison.Ordinal))
                continue;
            if (string.Equals(GetField(entry, provider), username, StringComparison.Ordinal))
                return false;
        }

        // Fill an empty provider slot on an existing entry for the same FideId when available.
        foreach (var entry in links)
        {
            if (!string.Equals(entry.FideId, fideId, StringComparison.Ordinal))
                continue;
            if (GetField(entry, provider) is null)
            {
                SetField(entry, provider, username);
                return true;
            }
        }

        // Otherwise append a new entry preserving all existing links.
        var added = new ChessFideLinkEntry { FideId = fideId };
        SetField(added, provider, username);
        links.Add(added);
        return true;
    }

    private static string? GetField(ChessFideLinkEntry entry, string provider) => provider switch
    {
        "chesscom" => entry.ChessCom,
        "lichess" => entry.Lichess,
        "fide" => entry.Alias,
        _ => null,
    };

    private static void SetField(ChessFideLinkEntry entry, string provider, string value)
    {
        switch (provider)
        {
            case "chesscom": entry.ChessCom = value; break;
            case "lichess": entry.Lichess = value; break;
            case "fide": entry.Alias = value; break;
        }
    }
}
