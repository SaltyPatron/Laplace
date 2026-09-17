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
    private sealed record SelectionReceipt(string Schema, FileIdentity Source,
        FileIdentity SelectionManifest, ChessCorpusEvidence.Chunk[] Chunks);
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
        var root = ChessCorpusEvidence.ReadSerialized<SelectionReceipt>(
            await File.ReadAllBytesAsync(manifest.Path, ct));
        if (root.Schema != "laplace.chess-recorded-selection/v1")
            throw new InvalidDataException("unsupported recorded selection schema");
        var source = ReadFile(root.Source);
        if (!source.Path.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("recorded selection requires its original plain PGN source");
        var selection = ReadFile(root.SelectionManifest);
        await ChessCorpusPreparation.RequireUnchangedAsync(Internal(source), ct);
        await ChessCorpusPreparation.RequireUnchangedAsync(Internal(selection), ct);

        var chunks = new List<RetainedChunk>();
        var playingIds = new HashSet<string>(StringComparer.Ordinal);
        int selected = 0;
        foreach (var item in root.Chunks)
        {
            ct.ThrowIfCancellationRequested();
            if (item is null) throw new InvalidDataException("retained chunk descriptor is null");
            int index = item.Index;
            int first = item.FirstSelectedGame;
            int count = item.Games;
            int novel = item.NovelGames;
            long plies = item.Plies;
            string? gameHash = item.GameBodiesSha256;
            if (index != chunks.Count + 1 || first != selected || count <= 0
                || count > 1_000_000 - selected || novel != count || plies < count || !Hex(gameHash, 64))
                throw new InvalidDataException("recorded selection contains a gap, incomplete chunk or invalid count");
            var body = ReadFile(item.Body is null ? null : Public(item.Body));
            var scope = ReadFile(item.Scope is null ? null : Public(item.Scope));
            await ChessCorpusPreparation.RequireUnchangedAsync(Internal(body), ct);
            await ChessCorpusPreparation.RequireUnchangedAsync(Internal(scope), ct);
            var value = ChessCorpusEvidence.ReadSerialized<ChessCorpusEvidence.ChunkBody>(
                await File.ReadAllBytesAsync(body.Path, ct));
            if (value.Schema != "laplace.chess-corpus-chunk/v1"
                || value.Index != index
                || value.FirstSelectedGame != first
                || value.NewlyRecordedGames != count)
                throw new InvalidDataException("retained chunk body does not match its selected manifest entry");
            var games = value.Games;
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
            var scopes = value.Scopes;
            if (scopes.Length != 1 || scopes[0] is null)
                throw new InvalidDataException("retained chunk must have one exact scope");
            ValidateScope(scopes[0]);
            var writer = ReadWriter(value.Writer);
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
                var entry = ChessCorpusEvidence.ReadSerialized<ChessCorpusPreparation.Selection>(
                    Encoding.UTF8.GetBytes(line));
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

    private static FileIdentity ReadFile(FileIdentity? value)
    {
        if (value is null || value.Path is null || !Path.IsPathFullyQualified(value.Path)
            || Path.GetFullPath(value.Path) != value.Path || value.Bytes < 0 || !Hex(value.Sha256, 64))
            throw new InvalidDataException("recorded selection file identity is invalid");
        return value;
    }

    internal static bool Hex(string? value, int length) => value is not null && value.Length == length
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

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

    private static ChessRecordingMeasurement.WriterCounts ReadWriter(ChessCorpusEvidence.WriterReceipt value)
    {
        if (value.RoundTripsKind != "logical-writer-accounting/v1")
            throw new InvalidDataException("unknown retained writer counter scope");
        if (new[] { value.ApplyCalls, value.EntitiesAttempted, value.EntitiesInserted,
                value.PhysicalitiesAttempted, value.PhysicalitiesInserted, value.AttestationsAttempted,
                value.AttestationsInserted, value.EntitiesSkippedAtMerge, value.PhysicalitiesSkippedAtMerge,
                value.RoundTrips, value.CopyTransactionsStarted, value.CopyTransactionsCommitted,
                value.JournalReplayHits }.Any(number => number < 0))
            throw new InvalidDataException("negative retained writer counter");
        return new()
        {
            ApplyCalls = value.ApplyCalls,
            EntitiesAttempted = value.EntitiesAttempted, EntitiesInserted = value.EntitiesInserted,
            PhysicalitiesAttempted = value.PhysicalitiesAttempted, PhysicalitiesInserted = value.PhysicalitiesInserted,
            AttestationsAttempted = value.AttestationsAttempted, AttestationsInserted = value.AttestationsInserted,
            EntitiesSkippedAtMerge = value.EntitiesSkippedAtMerge,
            PhysicalitiesSkippedAtMerge = value.PhysicalitiesSkippedAtMerge,
            RoundTrips = value.RoundTrips, CopyTransactionsStarted = value.CopyTransactionsStarted,
            CopyTransactionsCommitted = value.CopyTransactionsCommitted, JournalReplayHits = value.JournalReplayHits,
        };
    }
}
