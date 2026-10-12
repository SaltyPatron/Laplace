using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

// Request authority belongs to the retained operation, independently of the
// identities of its board positions, move trajectory and named participants.
public sealed record ChessPlayOperation(Guid SessionId, OperationPrincipal Requester,
    DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, bool RecordToSubstrate,
    string State, string Recording, string? Result, string? Fen, string[] Moves, string? Error)
{
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
