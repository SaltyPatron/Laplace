using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;
using Inventory = Laplace.Chess.Service.ChessStartingSideInventory;

namespace Laplace.Chess.Service;

/// <summary>Cold artifact export from the existing admitted read owner. It never deposits
/// testimony, changes a content recipe, or activates a serving cache generation.</summary>
internal static class ChessRecordedFloorExport
{
    internal const string Mode = "export-recorded-floors";
    internal const string MergeMode = "merge-transition-floors";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    internal sealed record Options(Inventory.Options Inventory, int TransitionBufferRecords, int MergeFanIn,
        long MaximumSpillBytes, long MaximumExportBytes);
    internal sealed record Artifact(string Role, string Path, long Bytes, string Sha256);
    internal sealed class Receipt
    {
        public string Schema => "laplace.chess-recorded-floor-export/v1";
        public string Status { get; set; } = "partial";
        public string ObservationScope => "observed-read-interval";
        public bool Snapshot => false;
        public required Options Bounds { get; init; }
        public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc { get; set; }
        public Inventory.DatabaseIdentity? DatabaseBefore { get; set; }
        public Inventory.DatabaseIdentity? DatabaseAfter { get; set; }
        public long? SelectedPlayings { get; set; }
        public long ExportedPlayings { get; set; }
        public long PositionOccurrences { get; set; }
        public long TransitionOccurrences { get; set; }
        public ulong? UniqueTransitions { get; set; }
        public long? TransitionPeakSpillBytes { get; set; }
        public long ExportWorkBytesReserved { get; set; }
        public bool InventoryComplete { get; set; }
        public List<Artifact> Files { get; } = [];
        public string? FailureType { get; set; }
        public string? FailureDetail { get; set; }
    }

    internal static Options Parse(string[] args)
    {
        var inventoryArgs = new List<string>();
        var extras = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length) throw new ArgumentException("Incomplete export option.");
            string key = args[i], value = args[i + 1];
            if (key is "--transition-buffer-records" or "--merge-fan-in" or "--maximum-spill-mib" or "--maximum-export-mib")
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number)
                    || number <= 0 || !extras.TryAdd(key, number))
                    throw new ArgumentException("Invalid or duplicate export bound.");
            }
            else { inventoryArgs.Add(key); inventoryArgs.Add(value); }
        }
        long buffer = extras.GetValueOrDefault("--transition-buffer-records", 65536);
        long fanIn = extras.GetValueOrDefault("--merge-fan-in", 32);
        if (buffer > int.MaxValue || fanIn < 2 || fanIn > 32)
            throw new ArgumentException("Transition buffer must fit an array and merge fan-in must be between 2 and 32.");
        return new Options(Inventory.Parse(inventoryArgs.ToArray()), (int)buffer, (int)fanIn,
            checked(extras.GetValueOrDefault("--maximum-spill-mib", 4096) * 1024 * 1024),
            checked(extras.GetValueOrDefault("--maximum-export-mib", 4096) * 1024 * 1024));
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("ChessCatalogSurfaces export-recorded-floors --output-dir <new-absolute-directory> "
                + "[inventory bounds] [--transition-buffer-records 65536] [--merge-fan-in 32] "
                + "[--maximum-spill-mib 4096] [--maximum-export-mib 4096]");
            return 0;
        }
        Options options;
        try { options = Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        { Console.Error.WriteLine(ex.Message); return 2; }
        try
        {
            await using var ds = LaplaceDataSource.Create(SubstrateAccess.Serving,
                Inventory.ReadOnlyConnectionString(LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Serving)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.Inventory.DeadlineSeconds));
            var receipt = await ExportAsync(options, new Inventory.DatabaseSource(ds), deadline.Token).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(receipt, Json));
            return receipt.Status == "completed" ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("Recorded floor export failed: " + ex.GetType().Name);
            return 1;
        }
    }

    internal static async Task<Receipt> ExportAsync(Options options, Inventory.IReadSource source, CancellationToken ct)
    {
        string directory = options.Inventory.OutputDirectory;
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("Export directory already exists.");
        Directory.CreateDirectory(directory);
        string receiptPath = Path.Combine(directory, "export-receipt.json");
        var receipt = new Receipt { Bounds = options, ExportWorkBytesReserved = 80 };
        using (var initial = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await JsonSerializer.SerializeAsync(initial, receipt, Json, CancellationToken.None).ConfigureAwait(false);
        string surfacesPath = Path.Combine(directory, "positions.txt");
        string transitionsPath = Path.Combine(directory, "transitions.bin");
        try
        {
            using var transitions = new ChessTransitionFloorBuilder(Path.Combine(directory, "transition-runs"),
                options.TransitionBufferRecords, options.MergeFanIn, options.MaximumSpillBytes);
            using (var surfaces = new FileStream(surfacesPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                var inventory = await Inventory.CollectAsync(options.Inventory with
                {
                    OutputDirectory = Path.Combine(directory, "inventory")
                }, source, ct, (game, _, token) =>
                {
                    ExportGame(game, surfaces, transitions, options.MaximumExportBytes, receipt, token);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                receipt.DatabaseBefore = inventory.DatabaseBefore;
                receipt.DatabaseAfter = inventory.DatabaseAfter;
                receipt.SelectedPlayings = inventory.SelectedBefore;
                receipt.InventoryComplete = inventory.Status == "completed";
                if (!receipt.InventoryComplete || inventory.Retained != receipt.ExportedPlayings)
                    throw new InvalidDataException("Recorded input inventory is incomplete; retain and inspect inventory/summary.json.");
                surfaces.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            var built = transitions.Complete(transitionsPath, ct);
            receipt.UniqueTransitions = built.Records;
            receipt.TransitionPeakSpillBytes = built.PeakSpillBytes;
            if (built.InputOccurrences != checked((ulong)receipt.TransitionOccurrences))
                throw new InvalidDataException("Transition producer occurrence count differs from verified replay.");
            foreach (var (role, path) in ArtifactPaths)
                receipt.Files.Add(await DescribeAsync(directory, role, path, ct).ConfigureAwait(false));
            if (receipt.Files.Where(file => file.Role is "position-surfaces" or "transition-floor").Sum(file => file.Bytes)
                > options.MaximumExportBytes)
                throw new InvalidDataException("Published export bytes exceed their admitted envelope.");
            receipt.Status = "completed";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            receipt.Status = "partial";
            receipt.FailureType = ex.GetType().Name;
            receipt.FailureDetail = ex is OperationCanceledException ? "Export deadline or cancellation was reached."
                : ex is InvalidDataException ? ex.Message[..Math.Min(ex.Message.Length, 512)]
                : "Export failed; inspect the inventory stage/type. Raw exception text is not retained.";
        }
        finally
        {
            receipt.FinishedUtc = DateTimeOffset.UtcNow;
            await WriteReceiptAsync(receiptPath, receipt).ConfigureAwait(false);
        }
        return receipt;
    }

    private static async Task WriteReceiptAsync(string path, Receipt receipt)
    {
        string temporary = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, receipt, Json, CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static readonly (string Role, string Path)[] ArtifactPaths =
    [
        ("position-surfaces", "positions.txt"), ("transition-floor", "transitions.bin"),
        ("inventory-summary", "inventory/summary.json"), ("witnessed-inputs", "inventory/hydrated-playings.jsonl")
    ];

    private static async Task<Artifact> DescribeAsync(string directory, string role, string relative, CancellationToken ct)
    {
        using var input = new FileStream(Path.Combine(directory, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
        long bytes = input.Length;
        byte[] digest = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        if (input.Length != bytes) throw new InvalidDataException("Export artifact changed during authentication.");
        return new Artifact(role, relative, bytes, Convert.ToHexString(digest).ToLowerInvariant());
    }

    internal static void ExportGame(ChessWitnessedGame game, Stream surfaces, ChessTransitionFloorBuilder transitions,
        long maximumExportBytes, Receipt receipt, CancellationToken ct)
    {
        var replay = game.AdmittedReplay
            ?? throw new InvalidDataException("Floor export requires the strict read owner's retained admitted replay.");
        if (replay.Truncated is not null || replay.Plies.Count != game.MoveIds.Count
            || game.Moves.Count != game.MoveIds.Count || game.StartPositionId is not { } start
            || ChessCompose.LineId(start, game.MoveIds.ToArray()) != game.LineId)
            throw new InvalidDataException("Floor export requires the complete witnessed line and typed move sequence.");
        var board = Board.FromFen(replay.StartFen);
        if (ChessCompose.PositionId(board) != start)
            throw new InvalidDataException("Retained replay start differs from the native recorded first constituent.");
        void Reserve(long bytes)
        {
            if (bytes < 0 || bytes > maximumExportBytes - receipt.ExportWorkBytesReserved)
                throw new InvalidDataException("Recorded floor export exceeds its admitted output-work byte envelope.");
            receipt.ExportWorkBytesReserved = checked(receipt.ExportWorkBytesReserved + bytes);
        }
        void Position(Board current)
        {
            ct.ThrowIfCancellationRequested();
            int ep = ChessModality.CapturableEpSquare(current);
            byte[] encoded = Encoding.UTF8.GetBytes(PositionContent.Surface(current,
                ep < 0 ? "-" : Board.SquareToAlgebraic(ep)) + "\n");
            Reserve(encoded.Length);
            surfaces.Write(encoded);
            receipt.PositionOccurrences = checked(receipt.PositionOccurrences + 1);
        }
        Position(board);
        var from = start;
        for (int index = 0; index < replay.Plies.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var ply = replay.Plies[index];
            byte[] encodedId = Convert.FromHexString(ply.PositionId);
            if (encodedId.Length != 16) throw new InvalidDataException("Invalid retained replay position id width.");
            var to = Hash128.FromBytes(encodedId);
            board = Board.FromFen(ply.Fen);
            if (ChessCompose.PositionId(board) != to || ply.Ply != index + 1)
                throw new InvalidDataException("Retained replay board does not match its canonical position/ordinal.");
            // Reserve an occurrence's maximum output bytes. Duplicate keys can only
            // reduce final storage; they never turn occurrences into new entities.
            Reserve(ChessTransitionFloor.RecordSize);
            transitions.Add(ChessCompose.TransitionKey(from, game.MoveIds[index]), to, ct);
            receipt.TransitionOccurrences = checked(receipt.TransitionOccurrences + 1);
            Position(board);
            from = to;
        }
        receipt.ExportedPlayings = checked(receipt.ExportedPlayings + 1);
    }

    internal static int Merge(string[] args)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var inputs = new List<string>();
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 == args.Length) throw new ArgumentException("Incomplete transition merge option.");
                string key = args[index], value = args[index + 1];
                if (key == "--input") inputs.Add(Path.GetFullPath(value));
                else if (key is not ("--output" or "--work-directory" or "--maximum-buffered-records"
                    or "--merge-fan-in" or "--maximum-spill-bytes" or "--deadline-seconds")
                    || !values.TryAdd(key, value))
                    throw new ArgumentException("Unknown or duplicate transition merge option.");
            }
            if (inputs.Count == 0 || !values.TryGetValue("--output", out var output)
                || !values.TryGetValue("--work-directory", out var work))
                throw new ArgumentException("Transition merge needs --output, --work-directory and --input.");
            long Bound(string name, long fallback)
            {
                if (!values.TryGetValue(name, out var text)) return fallback;
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value <= 0)
                    throw new ArgumentException("Transition merge bounds must be positive integers.");
                return value;
            }
            int buffered = checked((int)Bound("--maximum-buffered-records", 65536));
            int fanIn = checked((int)Bound("--merge-fan-in", 32));
            long spill = Bound("--maximum-spill-bytes", 4L * 1024 * 1024 * 1024);
            long seconds = Bound("--deadline-seconds", 3600);
            if (seconds > int.MaxValue / 1000 || fanIn < 2 || fanIn > 32)
                throw new ArgumentException("Transition merge deadline or fan-in is out of range.");
            Directory.CreateDirectory(work);
            string owned = Path.Combine(Path.GetFullPath(work),
                "merge-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            using var builder = new ChessTransitionFloorBuilder(owned, buffered, fanIn, spill);
            foreach (string input in inputs) builder.AddBlob(input, deadline.Token);
            var result = builder.Complete(output, deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "laplace.chess-transition-merge/v1", status = "completed",
                input_files = inputs, unique_transitions = result.Records,
                input_occurrences = result.InputOccurrences, peak_spill_bytes = result.PeakSpillBytes
            }));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("Transition floor merge failed: " + ex.GetType().Name);
            return ex is ArgumentException or OverflowException ? 2 : 1;
        }
    }
}
