using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>An immutable, hash-bound selection of complete retained recording chunks.
/// The original source and failed or completed parent receipt are never rewritten.</summary>
public sealed class ChessRecordedSelection
{
    public sealed record FileIdentity(string Path, long Bytes, string Sha256);
    public FileIdentity Manifest { get; }
    public FileIdentity Source { get; }
    public FileIdentity SelectionManifest { get; }
    public IReadOnlyList<Hash128> PlayingIds { get; }
    public int SelectedGames => PlayingIds.Count;
    public long Plies { get; }
    internal IReadOnlyList<ChessCorpusPreparation.Selection> Entries { get; }
    internal IReadOnlyList<RetainedChunk> Chunks { get; }
    internal sealed record RetainedChunk(ChessCorpusEvidence.Chunk Identity,
        ChessRecordingMeasurement.GameIdentity[] Games,
        ChessRecordingMeasurement.ScopeObservation[] Scopes,
        ChessRecordingMeasurement.WriterCounts Writer);

    private ChessRecordedSelection(FileIdentity manifest, FileIdentity source, FileIdentity selectionManifest,
        IReadOnlyList<ChessCorpusPreparation.Selection> entries, IReadOnlyList<RetainedChunk> chunks)
    {
        Manifest = manifest; Source = source; SelectionManifest = selectionManifest;
        Entries = Array.AsReadOnly(entries.ToArray());
        Chunks = Array.AsReadOnly(chunks.ToArray());
        PlayingIds = Array.AsReadOnly(entries.Select(e => Hash128.FromBytes(Convert.FromHexString(e.PlayingId))).ToArray());
        Plies = entries.Sum(e => (long)e.Plies);
    }

    internal static ChessCorpusPreparation.FileIdentity Internal(FileIdentity value)
        => new(value.Path, value.Bytes, value.Sha256);

    internal static FileIdentity Public(ChessCorpusPreparation.FileIdentity value)
        => new(value.Path, value.Bytes, value.Sha256);

    public async Task VerifyUnchangedAsync(CancellationToken ct = default)
    {
        await ChessCorpusPreparation.RequireUnchangedAsync(Internal(Source), ct);
        await VerifyRetainedEvidenceAsync(ct);
    }

    internal async Task VerifyRetainedEvidenceAsync(CancellationToken ct)
    {
        foreach (var file in new[] { Manifest, SelectionManifest })
            await ChessCorpusPreparation.RequireUnchangedAsync(Internal(file), ct);
        foreach (var chunk in Chunks)
        {
            await ChessCorpusPreparation.RequireUnchangedAsync(chunk.Identity.Body, ct);
            await ChessCorpusPreparation.RequireUnchangedAsync(chunk.Identity.Scope, ct);
        }
    }

    public static async Task<ChessRecordedSelection> LoadAsync(
        string manifestPath, string expectedSha256, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(manifestPath) || !Hex(expectedSha256, 64))
            throw new ArgumentException("recorded selection requires an absolute manifest path and exact lowercase SHA256");
        var manifest = await ChessCorpusPreparation.IdentifyAsync(manifestPath, ct);
        if (manifest.Sha256 != expectedSha256 || manifest.Bytes is <= 0 or > 32 * 1024 * 1024)
            throw new InvalidDataException("recorded selection manifest identity or size is invalid");
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(manifest.Path, ct));
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "laplace.chess-recorded-selection/v1")
            throw new InvalidDataException("unsupported recorded selection schema");
        var source = ReadFile(root.GetProperty("source"));
        if (!source.Path.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("recorded selection requires its original plain PGN source");
        var selection = ReadFile(root.GetProperty("selectionManifest"));
        await ChessCorpusPreparation.RequireUnchangedAsync(Internal(source), ct);
        await ChessCorpusPreparation.RequireUnchangedAsync(Internal(selection), ct);

        var chunks = new List<RetainedChunk>();
        var playingIds = new HashSet<string>(StringComparer.Ordinal);
        int selected = 0;
        foreach (var item in root.GetProperty("chunks").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            int index = item.GetProperty("index").GetInt32();
            int first = item.GetProperty("firstSelectedGame").GetInt32();
            int count = item.GetProperty("games").GetInt32();
            int novel = item.GetProperty("novelGames").GetInt32();
            long plies = item.GetProperty("plies").GetInt64();
            string? gameHash = item.GetProperty("gameBodiesSha256").GetString();
            if (index != chunks.Count + 1 || first != selected || count <= 0
                || count > 1_000_000 - selected || novel != count || plies < count || !Hex(gameHash, 64))
                throw new InvalidDataException("recorded selection contains a gap, incomplete chunk or invalid count");
            var body = ReadFile(item.GetProperty("body"));
            var scope = ReadFile(item.GetProperty("scope"));
            await ChessCorpusPreparation.RequireUnchangedAsync(Internal(body), ct);
            await ChessCorpusPreparation.RequireUnchangedAsync(Internal(scope), ct);
            using var chunkDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(body.Path, ct));
            RejectDuplicateProperties(chunkDocument.RootElement);
            var value = chunkDocument.RootElement;
            if (value.GetProperty("schema").GetString() != "laplace.chess-corpus-chunk/v1"
                || value.GetProperty("index").GetInt32() != index
                || value.GetProperty("firstSelectedGame").GetInt32() != first
                || value.GetProperty("newlyRecordedGames").GetInt32() != count)
                throw new InvalidDataException("retained chunk body does not match its selected manifest entry");
            var games = value.GetProperty("games").Deserialize<ChessRecordingMeasurement.GameIdentity[]>(
                ChessCorpusPreparation.Json) ?? throw new InvalidDataException("retained chunk has no game bodies");
            if (games.Length != count || games.Any(g => g is null || !Hex(g.PlayingId, 32)
                || !Hex(g.LineId, 32) || !Hex(g.StartPositionId, 32)
                || g.MoveIds is null || g.MoveIds.Length == 0 || g.MoveIds.Any(id => !Hex(id, 32))
                || g.Result is not ("1-0" or "0-1" or "1/2-1/2")
                || (g.WhitePlayerId is not null && !Hex(g.WhitePlayerId, 32))
                || (g.BlackPlayerId is not null && !Hex(g.BlackPlayerId, 32))
                || g.Termination is null || !playingIds.Add(g.PlayingId))
                || games.Sum(g => (long)g.MoveIds.Length) != plies
                || Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    games, ChessCorpusPreparation.Json))) != gameHash)
                throw new InvalidDataException("retained game inventory is incomplete, duplicate or changed");
            var scopes = value.GetProperty("scopes").Deserialize<ChessRecordingMeasurement.ScopeObservation[]>(
                ChessCorpusPreparation.Json) ?? throw new InvalidDataException("retained chunk has no exact scope");
            if (scopes.Length != 1 || scopes[0] is null)
                throw new InvalidDataException("retained chunk must have one exact scope");
            ValidateScope(scopes[0]);
            var writer = ReadWriter(value.GetProperty("writer"));
            if (writer.ApplyCalls < 1 || writer.JournalReplayHits != 0)
                throw new InvalidDataException("retained fresh chunk has no acknowledged writer work");
            chunks.Add(new(new(index, first, count, novel, plies, gameHash!,
                Internal(body), Internal(scope)), games, scopes, writer));
            selected += count;
        }
        if (selected == 0) throw new InvalidDataException("recorded selection contains no complete chunks");

        var entries = new List<ChessCorpusPreparation.Selection>(selected);
        using (var input = new StreamReader(selection.Path, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false))
        {
            foreach (var game in chunks.SelectMany(c => c.Games))
            {
                string? line = await input.ReadLineAsync(ct);
                if (line is null) throw new InvalidDataException("original source selection ends before retained chunks");
                using var entryDocument = JsonDocument.Parse(line);
                RejectDuplicateProperties(entryDocument.RootElement);
                var entry = entryDocument.RootElement.Deserialize<ChessCorpusPreparation.Selection>(
                    ChessCorpusPreparation.Json) ?? throw new InvalidDataException("invalid source selection entry");
                if (entry.SourceOrdinal <= (entries.Count == 0 ? 0 : entries[^1].SourceOrdinal)
                    || !Hex(entry.FramedGameSha256, 64) || entry.PlayingId != game.PlayingId
                    || entry.LineId != game.LineId || entry.StartPositionId != game.StartPositionId
                    || entry.Plies != game.MoveIds.Length || entry.Result != game.Result)
                    throw new InvalidDataException("retained chunk differs from its original source selection");
                entries.Add(entry);
            }
        }
        // Reuse the canonical framing/hash owner. The native identity/legal-line check
        // is performed by the ordinary recorded verifier, not a second PGN parser.
        int frames = 0;
        foreach (string _ in ChessCorpusPreparation.ReadSelected(source.Path, entries, ct)) frames++;
        if (frames != selected) throw new InvalidDataException("recorded source selection is incomplete");
        var result = new ChessRecordedSelection(Public(manifest), source, selection, entries, chunks);
        await result.VerifyUnchangedAsync(ct);
        return result;
    }

    private static FileIdentity ReadFile(JsonElement value)
    {
        string? path = value.GetProperty("path").GetString();
        string? hash = value.GetProperty("sha256").GetString();
        long bytes = value.GetProperty("bytes").GetInt64();
        if (path is null || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path
            || bytes < 0 || !Hex(hash, 64))
            throw new InvalidDataException("recorded selection file identity is invalid");
        return new(path, bytes, hash!);
    }

    internal static bool Hex(string? value, int length) => value is not null && value.Length == length
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("recorded selection JSON has duplicate properties");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) RejectDuplicateProperties(element);
    }

    private static void ValidateScope(ChessRecordingMeasurement.ScopeObservation scope)
    {
        if (scope.EntityIds is null || scope.PhysicalityIds is null || scope.WitnessIds is null
            || scope.Before is null || scope.After is null
            || scope.EntityIds.Concat(scope.PhysicalityIds).Concat(scope.WitnessIds).Any(id => !Hex(id, 32))
            || scope.Before.Concat(scope.After).Any(row => row is null || row.Kind is < 1 or > 3
                || !Hex(row.Id, 32) || row.ObservationCount < (row.Kind == 3 ? 1 : 0))
            || scope.Before.Select(row => (row.Kind, row.Id)).Distinct().Count() != scope.Before.Count
            || scope.Unchanged != ChessRecordingMeasurement.ScopeRowsEqual(scope.Before, scope.After))
            throw new InvalidDataException("retained chunk scope is invalid");
        ChessRecordingMeasurement.ValidateScopeCoverage(
            scope.EntityIds.Select(Convert.FromHexString).ToArray(),
            scope.PhysicalityIds.Select(Convert.FromHexString).ToArray(),
            scope.WitnessIds.Select(Convert.FromHexString).ToArray(), scope.After);
    }

    private static ChessRecordingMeasurement.WriterCounts ReadWriter(JsonElement value)
    {
        long Number(string key)
        {
            long number = value.GetProperty(key).GetInt64();
            return number >= 0 ? number : throw new InvalidDataException("negative retained writer counter");
        }
        if (value.GetProperty("roundTripsKind").GetString() != "logical-writer-accounting/v1")
            throw new InvalidDataException("unknown retained writer counter scope");
        return new()
        {
            ApplyCalls = Number("applyCalls"),
            EntitiesAttempted = Number("entitiesAttempted"), EntitiesInserted = Number("entitiesInserted"),
            PhysicalitiesAttempted = Number("physicalitiesAttempted"), PhysicalitiesInserted = Number("physicalitiesInserted"),
            AttestationsAttempted = Number("attestationsAttempted"), AttestationsInserted = Number("attestationsInserted"),
            EntitiesSkippedAtMerge = Number("entitiesSkippedAtMerge"),
            PhysicalitiesSkippedAtMerge = Number("physicalitiesSkippedAtMerge"),
            RoundTrips = Number("roundTrips"), CopyTransactionsStarted = Number("copyTransactionsStarted"),
            CopyTransactionsCommitted = Number("copyTransactionsCommitted"), JournalReplayHits = Number("journalReplayHits"),
        };
    }
}
