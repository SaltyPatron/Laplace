using System.Text;
using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>
/// Exact sorted scope folding with bounded open files and one row per input run.
/// These files are benchmark evidence, not an alternative substrate or identity store.
/// </summary>
internal static class ChessCorpusScopeMerge
{
    internal const int FanIn = 32;
    internal sealed record Row(short Kind, string Id, long? Before, long After, int FirstChunk, int LastChunk);
    internal sealed record State(short Kind, string Id, long ObservationCount);
    internal sealed record Result(ChessCorpusPreparation.FileIdentity File, long Rows);
    private static readonly JsonSerializerOptions Json = ChessCorpusPreparation.Json;
    private static readonly IComparer<(short Kind, string Id, int Run)> Order =
        Comparer<(short Kind, string Id, int Run)>.Create((left, right) =>
        {
            int compared = left.Kind.CompareTo(right.Kind);
            if (compared == 0) compared = StringComparer.Ordinal.Compare(left.Id, right.Id);
            return compared == 0 ? left.Run.CompareTo(right.Run) : compared;
        });

    internal static void Validate(Row row)
    {
        if (row.Kind is < 1 or > 3 || row.Id.Length != 32
            || row.Id.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))
            || row.FirstChunk < 1 || row.LastChunk < row.FirstChunk
            || (row.Kind == 3 ? row.After < 1 || row.Before is < 1
                : row.After != 0 || row.Before is not (null or 0))
            || (row.Before is { } prior && prior > row.After))
            throw new InvalidDataException("corpus scope row has an invalid identity/count/order");
    }

    internal static Row Fold(IReadOnlyList<Row> input)
    {
        if (input.Count == 0) throw new InvalidDataException("empty corpus scope fold");
        var rows = input.OrderBy(r => r.FirstChunk).ToArray();
        foreach (var row in rows) Validate(row);
        for (int i = 1; i < rows.Length; i++)
            if (rows[i].Kind != rows[0].Kind || rows[i].Id != rows[0].Id
                || rows[i].FirstChunk <= rows[i - 1].LastChunk
                || rows[i].Before != rows[i - 1].After)
                throw new InvalidDataException("corpus scope changed outside its observed chunk sequence");
        return rows[0] with { After = rows[^1].After, LastChunk = rows[^1].LastChunk };
    }

    private sealed class Cursor(string path) : IDisposable
    {
        private readonly StreamReader _reader = new(path, new UTF8Encoding(false, true));
        private Row? _previous;
        internal Row Current { get; private set; } = null!;
        internal async Task<bool> AdvanceAsync(CancellationToken ct)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line is null) return false;
            if (line.Length > 512)
                throw new InvalidDataException("corpus scope row exceeds its bounded transport shape");
            Current = JsonSerializer.Deserialize<Row>(line, Json)
                ?? throw new InvalidDataException("corpus scope row is absent");
            Validate(Current);
            if (_previous is not null && Order.Compare(
                    (_previous.Kind, _previous.Id, 0), (Current.Kind, Current.Id, 0)) >= 0)
                throw new InvalidDataException("corpus scope run is not strictly sorted and unique");
            _previous = Current;
            return true;
        }
        public void Dispose() => _reader.Dispose();
    }

    private static async Task MergeAsync(IReadOnlyList<string> inputs, string output, CancellationToken ct)
    {
        if (inputs.Count is < 1 or > FanIn)
            throw new InvalidDataException("corpus scope merge fan-in is outside its bound");
        var cursors = new List<Cursor>(inputs.Count);
        try
        {
            var queue = new PriorityQueue<int, (short Kind, string Id, int Run)>(Order);
            for (int i = 0; i < inputs.Count; i++)
            {
                var cursor = new Cursor(inputs[i]);
                cursors.Add(cursor);
                if (await cursor.AdvanceAsync(ct))
                    queue.Enqueue(i, (cursor.Current.Kind, cursor.Current.Id, i));
            }
            await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(file, new UTF8Encoding(false, true)) { NewLine = "\n" };
            while (queue.TryPeek(out _, out var key))
            {
                ct.ThrowIfCancellationRequested();
                var same = new List<Row>(inputs.Count);
                while (queue.TryPeek(out int index, out var next) && next.Kind == key.Kind && next.Id == key.Id)
                {
                    queue.Dequeue();
                    var cursor = cursors[index];
                    same.Add(cursor.Current);
                    if (await cursor.AdvanceAsync(ct))
                        queue.Enqueue(index, (cursor.Current.Kind, cursor.Current.Id, index));
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(Fold(same), Json).AsMemory(), ct);
            }
            await writer.FlushAsync(ct);
        }
        finally { foreach (var cursor in cursors) cursor.Dispose(); }
    }

    internal static async Task<Result> CompleteAsync(
        IReadOnlyList<string> inputs, string directory, CancellationToken ct)
    {
        if (inputs.Count == 0) throw new InvalidDataException("corpus recording has no scope chunks");
        Directory.CreateDirectory(directory);
        var runs = inputs.ToList();
        int pass = 0;
        do
        {
            var merged = new List<string>();
            for (int start = 0; start < runs.Count; start += FanIn)
            {
                string target = Path.Combine(directory, $"merge-{pass:D3}-{merged.Count:D7}.jsonl");
                await MergeAsync(runs.Skip(start).Take(FanIn).ToArray(), target, ct);
                merged.Add(target);
            }
            runs = merged;
            pass++;
        } while (runs.Count > 1);

        string state = Path.Combine(directory, "state.jsonl");
        long rows = 0;
        using (var cursor = new Cursor(runs[0]))
        await using (var file = new FileStream(state, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        await using (var writer = new StreamWriter(file, new UTF8Encoding(false, true)) { NewLine = "\n" })
        {
            while (await cursor.AdvanceAsync(ct))
            {
                var row = cursor.Current;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new State(row.Kind, row.Id, row.After), Json).AsMemory(), ct);
                rows++;
            }
            await writer.FlushAsync(ct);
        }
        return new(await ChessCorpusPreparation.IdentifyAsync(state, ct), rows);
    }
}
