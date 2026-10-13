using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

// Request authority belongs to the retained operation, independently of the
// identities of its board positions, move trajectory and named participants.
public sealed record ChessPlayOperation(Guid SessionId, OperationPrincipal Requester,
    DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, bool RecordToSubstrate,
    string State, string Recording, string? Result, string? Fen, string[] Moves, string? Error)
{
    public ChessPlayContext? Context { get; init; }
    public string? RecordedPlayingId { get; init; }
    public bool? Adjudicated { get; init; }
    public bool CanRetryRecording => RecordToSubstrate && State is "recording" or "failed"
        && Adjudicated is not null && Result is "1-0" or "0-1" or "1/2-1/2";
    public Dictionary<int, ChessLivePlyAnalysis> Analysis { get; init; } = new();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? PositionFens { get; init; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ChessPlayOperation? Read(Guid id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? JsonSerializer.Deserialize<ChessPlayOperation>(File.ReadAllText(path), Json) : null;
    }

    internal static ChessPlayOperation Start(Guid id, OperationPrincipal requester, bool record)
    {
        if (string.IsNullOrWhiteSpace(requester.TenantId) || string.IsNullOrWhiteSpace(requester.Subject))
            throw new ArgumentException("An authenticated play requester is required.");
        var now = DateTimeOffset.UtcNow;
        return new(id, requester, now, now, record, "ongoing", record ? "pending" : "disabled", null, null, [], null);
    }

    internal PlaySession Restore(bool resumeRecording = false)
    {
        if (State != "ongoing" && !(resumeRecording && CanRetryRecording))
            throw new InvalidOperationException("This operation cannot resume play or its retained recording.");
        // Earlier browser operations always started from the standard initial board.
        var context = Context ?? new ChessPlayContext("chess/play/session", null, "Browser player",
            Convert.ToHexString(ChessVocabulary.LaplacePlayerId.ToBytes()), "Laplace",
            new(Event: "Laplace Play", Site: "Laplace",
                Date: StartedUtc.ToString("yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture),
                StartFen: ChessModality.StartFen));
        var state = ReplayStates().Last();
        var session = new PlaySession(ChessVocabulary.PlaySessionHandle(SessionId), context.LearnContext,
            RecordToSubstrate, Requester.TenantId, Requester.Subject,
            ParsePlayer(context.WhitePlayerId), context.WhitePlayerName,
            ParsePlayer(context.BlackPlayerId), context.BlackPlayerName, Requester)
        { Operation = this with { Context = context }, Metadata = context.Metadata, State = state, PlyCount = Moves.Length };
        session.Moves.AddRange(Moves);
        return session;
    }

    private static Hash128? ParsePlayer(string? value) => value is null ? null : Hash128.FromBytes(Convert.FromHexString(value));

    public IEnumerable<ChessState> ReplayStates()
    {
        var modality = new ChessModality();
        var state = modality.FromFen(Context?.Metadata.StartFen ?? ChessModality.StartFen);
        yield return state;
        foreach (var uci in Moves)
        {
            var move = MoveGen.Legal(state.Board).Cast<ChessMove?>().FirstOrDefault(m => m!.Value.ToUci() == uci)
                ?? throw new InvalidDataException($"Retained play move '{uci}' is illegal.");
            state = modality.Apply(state, move);
            yield return state;
        }
        if (Fen is not null && state.Board.ToFen() != Fen)
            throw new InvalidDataException("Retained play trajectory disagrees with its final board.");
    }

    internal void Save()
    {
        var path = PathFor(SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, this, Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string PathFor(Guid id) => Path.Combine(ChessLabPaths.LabDir, "play", id.ToString("N") + ".json");
}

public sealed record ChessPlayContext(string LearnContext, string? WhitePlayerId, string? WhitePlayerName,
    string? BlackPlayerId, string? BlackPlayerName, ChessLiveGameMetadata Metadata);
