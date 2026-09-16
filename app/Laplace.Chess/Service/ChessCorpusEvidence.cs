using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>Streams one ordinary admission chunk at a time into exact retained evidence.</summary>
internal sealed class ChessCorpusEvidence
{
    internal sealed record Chunk(int Index, int FirstSelectedGame, int Games, int NovelGames, long Plies,
        string GameBodiesSha256, ChessCorpusPreparation.FileIdentity Body,
        ChessCorpusPreparation.FileIdentity Scope);
    public sealed record Receipt(string Directory, int Chunks, int ReadbackGames, int NewlyRecordedGames,
        ChessCorpusPreparation.FileIdentity? ChunkManifest, ChessCorpusScopeMerge.Result? ExactScopeState,
        bool Completed, string Scope);

    private readonly string _directory;
    private readonly string _manifest;
    private readonly ChessCorpusEvidence? _fresh;
    private readonly List<Chunk> _chunks = [];
    internal bool Completed { get; private set; }
    internal int ChunkCount => _chunks.Count;
    internal int ReadbackGames { get; private set; }
    internal int NewlyRecordedGames { get; private set; }
    private ChessCorpusPreparation.FileIdentity? _manifestIdentity;
    private ChessCorpusScopeMerge.Result? _state;
    public Receipt Summary => new(_directory, ChunkCount, ReadbackGames, NewlyRecordedGames,
        _manifestIdentity, _state, Completed,
        "Exact source game bodies and scoped entity/line-carrier/playing witness counts; bounded ordinary chunks, sorted disk manifests, no alternate database.");

    internal ChessCorpusEvidence(string directory, ChessCorpusEvidence? fresh = null)
    {
        if (System.IO.Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("corpus phase evidence directory already exists");
        System.IO.Directory.CreateDirectory(directory);
        _directory = directory;
        _fresh = fresh;
        _manifest = Path.Combine(directory, "chunks.jsonl");
        using var manifest = new FileStream(_manifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    internal static bool WriterIsZero(ChessRecordingMeasurement.WriterCounts writer) =>
        writer.ApplyCalls == 0 && writer.EntitiesAttempted == 0 && writer.EntitiesInserted == 0
        && writer.PhysicalitiesAttempted == 0 && writer.PhysicalitiesInserted == 0
        && writer.AttestationsAttempted == 0 && writer.AttestationsInserted == 0
        && writer.EntitiesSkippedAtMerge == 0 && writer.PhysicalitiesSkippedAtMerge == 0
        && writer.RoundTrips == 0 && writer.CopyTransactionsStarted == 0
        && writer.CopyTransactionsCommitted == 0 && writer.JournalReplayHits == 0;

    internal async Task AppendAsync(
        IReadOnlyList<ChessRecordingMeasurement.GameIdentity> games,
        IReadOnlyList<ChessRecordingMeasurement.ScopeObservation> scopes, int novel,
        ChessRecordingMeasurement.WriterCounts writer, CancellationToken ct)
    {
        if (Completed || games.Count == 0 || scopes.Count != 1 || novel < 0 || novel > games.Count)
            throw new InvalidDataException("corpus chunk evidence has an invalid shape");
        int index = _chunks.Count + 1;
        byte[] gameBytes = JsonSerializer.SerializeToUtf8Bytes(games, ChessCorpusPreparation.Json);
        string gameHash = Convert.ToHexStringLower(SHA256.HashData(gameBytes));
        if (_fresh is not null)
        {
            if (!_fresh.Completed || index > _fresh._chunks.Count
                || _fresh._chunks[index - 1].Games != games.Count
                || _fresh._chunks[index - 1].GameBodiesSha256 != gameHash
                || novel != 0 || !WriterIsZero(writer)
                || scopes.Any(s => !s.Unchanged || !ChessRecordingMeasurement.ScopeRowsEqual(s.Before, s.After)))
                throw new InvalidDataException("corpus replay changed a game body, exact scope or writer counter");
        }

        string bodyPath = Path.Combine(_directory, $"chunk-{index:D7}.json");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "laplace.chess-corpus-chunk/v1", index, firstSelectedGame = ReadbackGames,
            games, newlyRecordedGames = novel, writer, scopes,
        }, ChessCorpusPreparation.Json);
        await using (var file = new FileStream(bodyPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await file.WriteAsync(body, ct);

        var scope = scopes[0];
        var before = scope.Before.ToDictionary(row => (row.Kind, row.Id), row => row.ObservationCount);
        var after = scope.After.OrderBy(row => row.Kind).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var afterIds = after.Select(row => (row.Kind, row.Id)).ToHashSet();
        if (afterIds.Count != after.Length || before.Keys.Any(id => !afterIds.Contains(id)))
            throw new InvalidDataException("corpus scope lost or duplicated a selected row");
        string scopePath = Path.Combine(_directory, $"scope-{index:D7}.jsonl");
        await using (var file = new FileStream(scopePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        await using (var output = new StreamWriter(file, new UTF8Encoding(false, true)) { NewLine = "\n" })
        {
            foreach (var row in after)
            {
                var delta = new ChessCorpusScopeMerge.Row(row.Kind, row.Id,
                    before.TryGetValue((row.Kind, row.Id), out long count) ? count : null,
                    row.ObservationCount, index, index);
                ChessCorpusScopeMerge.Validate(delta);
                await output.WriteLineAsync(JsonSerializer.Serialize(delta, ChessCorpusPreparation.Json).AsMemory(), ct);
            }
            await output.FlushAsync(ct);
        }
        var entry = new Chunk(index, ReadbackGames, games.Count, novel, games.Sum(g => (long)g.MoveIds.Length),
            gameHash, new(Path.GetFullPath(bodyPath), body.LongLength, Convert.ToHexStringLower(SHA256.HashData(body))),
            await ChessCorpusPreparation.IdentifyAsync(scopePath, ct));
        await File.AppendAllTextAsync(_manifest, JsonSerializer.Serialize(entry, ChessCorpusPreparation.Json) + "\n", ct);
        _chunks.Add(entry);
        ReadbackGames += games.Count;
        NewlyRecordedGames += novel;
    }
    internal async Task VerifyRetainedAsync(CancellationToken ct)
    {
        if (!Completed || _manifestIdentity is null || _state is null)
            throw new InvalidDataException("corpus baseline is not sealed");
        await ChessCorpusPreparation.RequireUnchangedAsync(_manifestIdentity, ct);
        await ChessCorpusPreparation.RequireUnchangedAsync(_state.File, ct);
        foreach (var chunk in _chunks)
        {
            await ChessCorpusPreparation.RequireUnchangedAsync(chunk.Body, ct);
            await ChessCorpusPreparation.RequireUnchangedAsync(chunk.Scope, ct);
        }
    }

    internal async Task CompleteAsync(CancellationToken ct)
    {
        if (Completed || _chunks.Count == 0)
            throw new InvalidDataException("corpus evidence has no unsealed chunks");
        if (_fresh is not null) await _fresh.VerifyRetainedAsync(ct);
        if (_fresh is not null && _chunks.Count != _fresh._chunks.Count)
            throw new InvalidDataException("corpus replay did not cover every original chunk");
        _manifestIdentity = await ChessCorpusPreparation.IdentifyAsync(_manifest, ct);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long expectedBytes = 0;
        foreach (var chunk in _chunks)
        {
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(chunk, ChessCorpusPreparation.Json) + "\n");
            expected.AppendData(line);
            expectedBytes += line.Length;
        }
        if (_manifestIdentity.Bytes != expectedBytes
            || _manifestIdentity.Sha256 != Convert.ToHexStringLower(expected.GetHashAndReset()))
            throw new InvalidDataException("corpus chunk manifest differs from the observed chunk sequence");
        foreach (var chunk in _chunks)
        {
            await ChessCorpusPreparation.RequireUnchangedAsync(chunk.Body, ct);
            await ChessCorpusPreparation.RequireUnchangedAsync(chunk.Scope, ct);
        }
        _state = await ChessCorpusScopeMerge.CompleteAsync(
            _chunks.Select(chunk => chunk.Scope.Path).ToArray(), Path.Combine(_directory, "scope-fold"), ct);
        foreach (var chunk in _chunks)
            await ChessCorpusPreparation.RequireUnchangedAsync(chunk.Scope, ct);
        if (_fresh is not null && (_fresh._state is null || _state.Rows != _fresh._state.Rows
            || _state.File.Bytes != _fresh._state.File.Bytes || _state.File.Sha256 != _fresh._state.File.Sha256))
            throw new InvalidDataException("corpus post-pool scope changed before or during exact replay");
        await ChessCorpusPreparation.RequireUnchangedAsync(_manifestIdentity, ct);
        Completed = true;
    }
}
