using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>Cold verification in its own CLI process. Replays authenticated export
/// inputs through the existing canonical owner; never records or invents games.</summary>
internal static class ChessRecordedFloorWitness
{
    internal const string Mode = "select-recorded-floor-witness";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    internal sealed record Options(string WitnessedInputs, string SeedPosition, string SeedTransition,
        string InstalledPosition, string InstalledTransition, string Output,
        int MaximumLineBytes = 8 * 1024 * 1024, long MaximumReplayBytes = 128L * 1024 * 1024,
        int SkipEligible = 0);
    // The inventory owns this Pascal-case retained-record transport. Decode only
    // its declared fields; canonical chess interpretation stays in the typed replay.
    internal sealed record RetainedPlaying(string PlayingId, string LineId, string StartPositionId,
        string? StartFen, string[] MoveIds, string[] Moves, string Result);

    internal sealed record FileFact(string Path, long Bytes, string Sha256);
    internal sealed record Selected(string PlayingId, string LineId, string StartPositionId,
        int Ply, string FromFen, string MoveUci, string FromPositionId, string MoveId,
        string TransitionKey, string ToPositionId, string ToFen, IReadOnlyList<string> LegalUci);
    internal sealed record Candidate(Selected Selected, ChessNode Position);
    internal sealed record Result(string Schema, string Status, Selected Selected,
        IReadOnlyDictionary<string, FileFact> Files, object Seed, object Installed,
        long ObservedPlayings, long EligibleTransitions, object Bounds,
        IReadOnlyList<FileFact> NativeModules);

    internal static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] paths = ["--witnessed-inputs", "--seed-position", "--seed-transition",
            "--installed-position", "--installed-transition", "--output"];
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || (!paths.Contains(args[i]) && args[i] is not
                ("--maximum-line-bytes" or "--maximum-replay-bytes" or "--skip-eligible"))
                || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown, duplicate or incomplete witness option.");
        }
        foreach (string path in paths)
            if (!values.TryGetValue(path, out var value) || !Path.IsPathFullyQualified(value))
                throw new ArgumentException(path + " must be an absolute path.");
        long Number(string name, long fallback, bool zero = false)
        {
            if (!values.TryGetValue(name, out var value)) return fallback;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result)
                || result < (zero ? 0 : 1)) throw new ArgumentException("Invalid witness bound.");
            return result;
        }
        long line = Number("--maximum-line-bytes", 8 * 1024 * 1024);
        if (line > 64 * 1024 * 1024) throw new ArgumentException("Witness line allowance exceeds 64 MiB.");
        return new(values[paths[0]], values[paths[1]], values[paths[2]], values[paths[3]],
            values[paths[4]], values[paths[5]], (int)line, Number("--maximum-replay-bytes", 128L * 1024 * 1024),
            checked((int)Number("--skip-eligible", 0, true)));
    }

    internal static int Run(string[] args)
    {
        try
        {
            Options options = Parse(args);
            if (File.Exists(options.Output) || Directory.Exists(options.Output))
                throw new IOException("Witness output must be new.");
            Result receipt = Verify(options);
            using var output = new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, receipt, Json);
            output.Flush(flushToDisk: true);
            Console.WriteLine(JsonSerializer.Serialize(receipt, Json));
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine("Recorded floor witness refused: " + error.GetType().Name + ": " + error.Message);
            return error is ArgumentException or OverflowException ? 2 : 1;
        }
    }

    internal static FileFact Fact(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long bytes = file.Length;
        string hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        if (bytes != file.Length) throw new InvalidDataException("Witness input changed while hashing.");
        return new(Path.GetFullPath(path), bytes, hash);
    }

    internal static Result Verify(Options options)
    {
        var files = new Dictionary<string, FileFact>
        {
            ["witnessed_inputs"] = Fact(options.WitnessedInputs),
            ["seed_position"] = Fact(options.SeedPosition), ["seed_transition"] = Fact(options.SeedTransition),
            ["installed_position"] = Fact(options.InstalledPosition),
            ["installed_transition"] = Fact(options.InstalledTransition)
        };
        ChessCompose.InitializePerfcaches();
        ChessPositionFloor.Load(options.SeedPosition);
        ChessTransitionFloor.Load(options.SeedTransition); // replaces any process-local Remember state
        Candidate? selected = null;
        long observed = 0, eligible = 0;
        try
        {
            foreach (var line in Lines(options.WitnessedInputs, options.MaximumLineBytes))
            {
                observed++;
                var budget = new NpgsqlSubstrateReads.ChessWitnessReadBudget(options.MaximumReplayBytes);
                budget.Reserve(checked(65536L + line.Length * 16L));
                var playing = JsonSerializer.Deserialize<RetainedPlaying>(line.Span)
                    ?? throw new InvalidDataException("Witness playing is null.");
                selected = Select(playing, budget, options, ref eligible);
                if (selected is not null) break;
            }
            if (selected is null)
                throw new InvalidDataException("No admitted legal corpus transition with a source position absent from both seed floors was found.");
            var choice = selected.Selected;
            ChessPositionFloor.Load(options.InstalledPosition);
            if (!ChessPositionFloor.TryLookup(Id(choice.FromPositionId), out double x, out double y,
                out double z, out double m, out var hilbert, out uint n, out byte tier))
                throw new InvalidDataException("Selected corpus position is absent from installed native floor.");
            double[] actual = [x, y, z, m];
            var canonical = selected.Position;
            for (int i = 0; i < 4; i++)
                if (BitConverter.DoubleToInt64Bits(actual[i]) != BitConverter.DoubleToInt64Bits(canonical.Coord[i]))
                    throw new InvalidDataException("Installed position geometry differs from canonical native composition.");
            if (!hilbert.Equals(canonical.Hb) || n != canonical.NConstituents || tier != canonical.Tier)
                throw new InvalidDataException("Installed position Hilbert/count/tier differs from canonical composition.");
            ChessTransitionFloor.Load(options.InstalledTransition);
            if (!ChessTransitionFloor.TryLookup(Id(choice.TransitionKey), out var to, out var source)
                || source != ChessTransitionFloor.LookupSource.Persistent || to != Id(choice.ToPositionId))
                throw new InvalidDataException("Installed transition is not an exact persistent canonical hit.");
            foreach (var fact in files.Values)
                if (Fact(fact.Path) != fact) throw new InvalidDataException("Witness input changed during verification.");
            using var process = Process.GetCurrentProcess();
            var modules = process.Modules.Cast<ProcessModule>()
                .Where(module => module.ModuleName.Contains("laplace_core", StringComparison.Ordinal))
                .Select(module => Fact(module.FileName)).Distinct().ToArray();
            if (modules.Length != 1) throw new InvalidDataException("Exactly one loaded native core is required.");
            return new("laplace.chess-recorded-floor-witness/v1", "passed", choice, files,
                new { position_absent = true, transition_absent = true },
                new { position_verified = true, transition_lookup_source = "Persistent",
                    coordinate_bits = actual.Select(BitConverter.DoubleToInt64Bits).ToArray(),
                    physicality_id = Hex(canonical.PhysId), n_constituents = n, tier },
                observed, eligible,
                new { options.MaximumLineBytes, options.MaximumReplayBytes, options.SkipEligible }, modules);
        }
        finally { ChessPositionFloor.Unload(); ChessTransitionFloor.Unload(); }
    }

    internal static string Hex(Hash128 value) => Convert.ToHexString(value.ToBytes()).ToLowerInvariant();

    internal static Hash128 Id(string value)
    {
        // Existing witnessed JSONL uses the record struct's exact ToString transport;
        // replay positions use explicit byte hex. These are encodings of the same 128 bits.
        const string prefix = "Hash128 { Hi = ", middle = ", Lo = ", suffix = " }";
        if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith(suffix, StringComparison.Ordinal))
        {
            string[] parts = value[prefix.Length..^suffix.Length].Split(middle, StringSplitOptions.None);
            if (parts.Length == 2
                && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong high)
                && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong low))
            {
                var parsed = new Hash128(high, low);
                if (parsed.ToString() == value) return parsed;
            }
            throw new InvalidDataException("Noncanonical witnessed identity transport.");
        }
        byte[] bytes = Convert.FromHexString(value);
        if (bytes.Length != 16) throw new InvalidDataException("Witness identity width is not 128 bits.");
        return Hash128.FromBytes(bytes);
    }

    internal static Candidate? Select(RetainedPlaying value, NpgsqlSubstrateReads.ChessWitnessReadBudget budget,
        Options options, ref long eligible)
    {
        if (value.PlayingId is null || value.LineId is null || value.StartPositionId is null
            || value.MoveIds is null || value.Moves is null || value.Result is null
            || value.MoveIds.Any(item => item is null) || value.Moves.Any(item => item is null))
            throw new InvalidDataException("Witness playing lacks a declared retained field.");
        var moves = value.MoveIds;
        budget.Reserve(checked((moves.Length + 1L) * 4096L));
        string playing = value.PlayingId;
        string lineId = value.LineId;
        string startId = value.StartPositionId;
        _ = Id(playing);
        var ids = moves.Select(Id).ToArray();
        string? fen = value.StartFen;
        var start = Board.FromFen(fen ?? ChessModality.StartFen);
        var startHash = ChessCompose.PositionId(start);
        if (startHash != Id(startId) || ChessCompose.LineId(startHash, ids) != Id(lineId))
            throw new InvalidDataException("Witness start/LINE differs from canonical content.");
        string result = value.Result;
        if (result is not ("1-0" or "0-1" or "1/2-1/2"))
            throw new InvalidDataException("Witness playing lacks a completed recorded result.");
        var replay = ChessWitnessHydrator.ReplayAdmittedLine(ids, fen);
        if (replay.Truncated is not null || replay.Plies.Count != ids.Length)
            throw new InvalidDataException("Witness typed line did not replay completely.");
        var sans = value.Moves;
        if (sans.Length != ids.Length) throw new InvalidDataException("Witness SAN count differs.");
        for (int i = 0; i < ids.Length; i++)
            if (sans[i] != replay.Plies[i].San)
                throw new InvalidDataException("Witness SAN differs from canonical typed replay.");
        string fromFen = replay.StartFen;
        Hash128 fromId = startHash;
        for (int i = 0; i < ids.Length; i++)
        {
            var ply = replay.Plies[i];
            var key = ChessCompose.TransitionKey(fromId, ids[i]);
            var board = Board.FromFen(fromFen);
            // A tablebase early return or terminal root would not exercise the API's
            // ordinary substrate frontier. Keep this acceptance request in that owner.
            bool ordinarySearch = board.Squares.Count(piece => piece != Piece.Empty) > 7
                && new ChessModality().Terminal(new ChessModality().FromFen(fromFen)) is null;
            bool absentPosition = !ChessPositionFloor.TryLookup(fromId, out _, out _, out _, out _, out _, out _, out _);
            bool absentTransition = !ChessTransitionFloor.TryLookup(key, out _);
            if (ordinarySearch && absentPosition && absentTransition)
            {
                if (eligible++ >= options.SkipEligible)
                {
                    string[] legal = MoveGen.Legal(board).Select(move => move.ToUci()).ToArray();
                    if (!legal.Contains(ply.Uci, StringComparer.Ordinal))
                        throw new InvalidDataException("Replayed corpus move is not a legal frontier member.");
                    // The CLI starts in a fresh process and has only requested identities
                    // so far. Removing the map forces existing canonical native geometry,
                    // instead of comparing a floor lookup against itself.
                    ChessPositionFloor.Unload();
                    var composed = ChessCompose.Position(board).Position;
                    if (composed.Id != fromId) throw new InvalidDataException("Canonical position changed during composition.");
                    return new(new(Hex(Id(playing)), Hex(Id(lineId)), Hex(Id(startId)), i + 1, fromFen, ply.Uci,
                        Hex(fromId), Hex(ids[i]), Hex(key), ply.PositionId,
                        ply.Fen, legal), composed);
                }
            }
            fromFen = ply.Fen;
            fromId = Id(ply.PositionId);
        }
        return null;
    }

    // Fixed buffers bound each JSONL frame before typed decoding or replay allocation.
    internal static IEnumerable<ReadOnlyMemory<byte>> Lines(string path, int maximumLineBytes)
    {
        byte[] line = new byte[maximumLineBytes], block = new byte[65536];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int used = 0, read;
        while ((read = input.Read(block)) != 0)
        {
            int offset = 0;
            while (offset < read)
            {
                int newline = Array.IndexOf(block, (byte)'\n', offset, read - offset);
                int take = (newline < 0 ? read : newline) - offset;
                if (take > maximumLineBytes - used) throw new InvalidDataException("Witness JSONL frame exceeds its byte allowance.");
                block.AsSpan(offset, take).CopyTo(line.AsSpan(used));
                used += take; offset += take;
                if (newline < 0) break;
                if (used == 0) throw new InvalidDataException("Witness JSONL has an empty frame.");
                yield return line.AsMemory(0, used);
                used = 0; offset++;
            }
        }
        if (used != 0) throw new InvalidDataException("Witness JSONL has an incomplete trailing frame.");
    }
}
