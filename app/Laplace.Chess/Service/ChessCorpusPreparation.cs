using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>Bound source selection, never a generator or an alternative PGN parser.</summary>
internal sealed class ChessCorpusPreparation
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record FileIdentity(string Path, long Bytes, string Sha256);
    internal sealed record Selection(long SourceOrdinal, string FramedGameSha256, string PlayingId,
        string LineId, string StartPositionId, int Plies, string Result);
    internal sealed class Counts
    {
        public long SourceGamesScanned { get; internal set; }
        public long CompleteLegalSourceGames { get; internal set; }
        public long RejectedGames { get; internal set; }
        public long EmptyGames { get; internal set; }
        public long DuplicateSelectionCandidates { get; internal set; }
        public long AlreadyPresentGames { get; internal set; }
        public long EligibleNovelGamesBeyondRequest { get; internal set; }
        public int SelectedGames { get; internal set; }
    }
    public FileIdentity Source { get; }
    public FileIdentity SelectionManifest { get; private set; } = null!;
    public Counts Inventory { get; }
    public string Framing => "existing PgnGames.StreamGames, strict UTF-8, original headers and full movetext, UTF-8 BOM removed if present and line endings normalized to LF";
    public string ProvenanceScope => "Observed immutable local source bytes and original PGN occurrence metadata; this receipt does not authenticate a remote download.";
    public int DistinctLines { get; private set; }
    public int DistinctStartPositions { get; private set; }
    public long Plies { get; private set; }
    public int MinimumPlies { get; private set; }
    public int MaximumPlies { get; private set; }
    internal List<Selection> Selected { get; } = [];

    private ChessCorpusPreparation(FileIdentity source, Counts inventory)
        => (Source, Inventory) = (source, inventory);

    internal static string HashText(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string Id(Hash128 id) => Convert.ToHexStringLower(id.ToBytes());

    internal static async Task<FileIdentity> IdentifyAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long bytes = file.Length;
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (file.Length != bytes)
            throw new InvalidDataException("corpus artifact length changed during hashing");
        return new(Path.GetFullPath(path), bytes, hash);
    }

    internal static async Task RequireUnchangedAsync(FileIdentity expected, CancellationToken ct)
    {
        if (await IdentifyAsync(expected.Path, ct) != expected)
            throw new InvalidDataException("bound corpus artifact bytes changed: " + expected.Path);
    }

    internal async Task VerifyAsync(CancellationToken ct)
    {
        await RequireUnchangedAsync(Source, ct);
        await RequireUnchangedAsync(SelectionManifest, ct);
    }

    internal static async Task<ChessCorpusPreparation> PrepareAsync(
        ChessCorpusBenchmark.Options options, ChessPgnIngestor ingestor, CancellationToken ct)
    {
        var source = await IdentifyAsync(options.PgnPath, ct);
        if (options.ExpectedSha256 is { } expected && source.Sha256 != expected)
            throw new InvalidDataException("source PGN SHA256 differs from the requested identity");
        var value = new ChessCorpusPreparation(source, new Counts());
        try
        {
        string manifest = Path.Combine(options.EvidenceDirectory, "selection.jsonl");
        var selectedIds = new HashSet<Hash128>();
        var parsed = new List<ChessGameRecord>(ChessPgnIngestor.ResolvedGamesPerChunk);
        var ordinals = new Dictionary<Hash128, long>();
        await using (var file = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        await using (var writer = new StreamWriter(file, new UTF8Encoding(false, true)))
        {
            async Task SelectChunkAsync()
            {
                int eligible = parsed.Count, novel = 0;
                await foreach (var game in ingestor.SelectNovelAsync(parsed, ct))
                {
                    novel++;
                    if (value.Selected.Count == options.Games)
                    {
                        value.Inventory.EligibleNovelGamesBeyondRequest++;
                        continue;
                    }
                    var selected = Describe(ordinals[game.PlayingId], game);
                    value.Selected.Add(selected);
                    selectedIds.Add(game.PlayingId);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(selected, Json).AsMemory(), ct);
                }
                value.Inventory.AlreadyPresentGames += eligible - novel;
                parsed.Clear();
                ordinals.Clear();
                await writer.FlushAsync(ct);
            }

            foreach (string gameText in PgnGames.StreamGames(source.Path, requireUtf8: true))
            {
                ct.ThrowIfCancellationRequested();
                value.Inventory.SourceGamesScanned++;
                ChessGameRecord? game;
                try { game = ChessPgnDecomposer.TryParseGame(gameText, requireCompleteSource: true); }
                catch (InvalidDataException) { value.Inventory.RejectedGames++; continue; }
                if (game is null) { value.Inventory.RejectedGames++; continue; }
                value.Inventory.CompleteLegalSourceGames++;
                if (game.MoveIds.Length == 0) { value.Inventory.EmptyGames++; continue; }
                if (selectedIds.Contains(game.PlayingId) || ordinals.ContainsKey(game.PlayingId))
                {
                    value.Inventory.DuplicateSelectionCandidates++;
                    continue;
                }
                parsed.Add(game);
                ordinals.Add(game.PlayingId, value.Inventory.SourceGamesScanned);
                if (parsed.Count < ChessPgnIngestor.ResolvedGamesPerChunk) continue;
                await SelectChunkAsync();
                if (value.Selected.Count == options.Games) break;
            }
            if (parsed.Count > 0 && value.Selected.Count < options.Games) await SelectChunkAsync();
        }
        value.Inventory.SelectedGames = value.Selected.Count;
        value.DistinctLines = value.Selected.Select(s => s.LineId).Distinct(StringComparer.Ordinal).Count();
        value.DistinctStartPositions = value.Selected.Select(s => s.StartPositionId).Distinct(StringComparer.Ordinal).Count();
        value.Plies = value.Selected.Sum(s => (long)s.Plies);
        value.MinimumPlies = value.Selected.Count == 0 ? 0 : value.Selected.Min(s => s.Plies);
        value.MaximumPlies = value.Selected.Count == 0 ? 0 : value.Selected.Max(s => s.Plies);
        value.SelectionManifest = await IdentifyAsync(manifest, ct);
        await RequireUnchangedAsync(source, ct);
        return value;
        }
        catch (Exception error)
        {
            value.Inventory.SelectedGames = value.Selected.Count;
            await File.WriteAllTextAsync(Path.Combine(options.EvidenceDirectory, "preparation-partial.json"),
                JsonSerializer.Serialize(new { status = "failed", error = error.Message,
                    errorType = error.GetType().Name, source = value }, Json));
            throw;
        }
    }

    internal static Selection Describe(long ordinal, ChessGameRecord game)
        => new(ordinal, HashText(game.GameText), Id(game.PlayingId), Id(game.LineId),
            Id(game.PositionIds[0]), game.MoveIds.Length, game.Result.ResultToken);

    internal IEnumerable<string> ReadSelected(CancellationToken ct)
        => ReadSelected(Source.Path, Selected, ct);

    internal static IEnumerable<string> ReadSelected(
        string path, IReadOnlyList<Selection> entries, CancellationToken ct)
    {
        long previous = 0;
        var playings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.SourceOrdinal <= previous || !playings.Add(entry.PlayingId))
                throw new InvalidDataException("corpus selection has reordered or duplicate original occurrences");
            previous = entry.SourceOrdinal;
        }
        if (entries.Count == 0) yield break;
        long ordinal = 0;
        int selected = 0;
        foreach (string game in PgnGames.StreamGames(path, requireUtf8: true))
        {
            ct.ThrowIfCancellationRequested();
            ordinal++;
            var expected = entries[selected];
            if (ordinal != expected.SourceOrdinal) continue;
            if (HashText(game) != expected.FramedGameSha256)
                throw new InvalidDataException("selected original game text differs from the prepared source");
            selected++;
            yield return game;
            if (selected == entries.Count) yield break;
        }
        if (selected != entries.Count)
            throw new InvalidDataException("source corpus ended before all prepared occurrences were read");
    }

    internal void ValidateParsed(int index, ChessGameRecord game)
    {
        if (index < 0 || index >= Selected.Count || !game.CompleteSourceVerified)
            throw new InvalidDataException("corpus game lacks complete-source native verification");
        ValidateSelection(Selected[index], game);
    }

    internal static void ValidateSelection(Selection expected, ChessGameRecord game)
    {
        if (!game.CompleteSourceVerified)
            throw new InvalidDataException("corpus game lacks complete-source native verification");
        if (Id(game.PlayingId) != expected.PlayingId || Id(game.LineId) != expected.LineId
            || Id(game.PositionIds[0]) != expected.StartPositionId || game.MoveIds.Length != expected.Plies
            || game.Result.ResultToken != expected.Result || HashText(game.GameText) != expected.FramedGameSha256)
            throw new InvalidDataException("native corpus occurrence differs from the prepared original game");
        string plyCount = PgnGames.TagStr(game.GameText, "PlyCount");
        if (plyCount.Length > 0 && (!int.TryParse(plyCount, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int declared) || declared != expected.Plies))
            throw new InvalidDataException("corpus PlyCount differs from its complete legal trajectory");
    }
}
