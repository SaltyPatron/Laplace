using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;

/// <summary>
/// Build-time input for the chess perfcaches, produced before native emit:
/// 1) the tier-2 board surfaces of the position floor;
/// 2) the (from, move) → to transition map, a read-only map over position identities.
/// Also dispatches the recorded-floor and GUI-evidence export modes.
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception failure)
        {
            // CMake/deployment captures stderr. Predictable tool failures must
            // return an error instead of escaping into a Windows CLR dialog.
            Laplace.Ops.LaplaceLogging.ReportProcessFailure("ChessCatalogSurfaces", failure);
            return 1;
        }
    }

    static int Run(string[] args)
    {
        if (args.FirstOrDefault() == ChessGuiGameEvidence.Mode)
            return ChessGuiGameEvidence.Run(args[1..]);
        if (args.FirstOrDefault() == ChessRecordedFloorWitness.Mode)
            return ChessRecordedFloorWitness.Run(args[1..]);
        if (args.FirstOrDefault() == ChessStartingSideInventory.Mode)
            return ChessStartingSideInventory.RunAsync(args[1..]).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == ChessRecordedFloorExport.Mode)
            return ChessRecordedFloorExport.RunAsync(args[1..]).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == ChessRecordedFloorExport.MergeMode)
            return ChessRecordedFloorExport.Merge(args[1..]);

        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: ChessCatalogSurfaces <openings-dir-or-NONE> <surfaces-out> [transitions-out]");
            return 2;
        }
        string openings = args[0];
        string surfacesOut = args[1];
        string? transitionsOut = args.ElementAtOrDefault(2);
        if (string.IsNullOrEmpty(transitionsOut))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(surfacesOut));
            transitionsOut = Path.Combine(dir ?? ".", "laplace_chess_transition_perfcache.bin");
        }

        var surfaces = new SortedSet<string>(StringComparer.Ordinal);
        var transitions = new Dictionary<Hash128, Hash128>(); // key → to
        var m = new ChessModality();

        void NotePosition(Board board, Hash128 id)
        {
            surfaces.Add(PositionContent.Surface(board, Ep(board)));
            _ = id;
        }

        void WalkLine(IReadOnlyList<string> sans, ChessState start, string origin = "standard seed")
        {
            var board = start.Board.Clone();
            var fromId = ChessCompose.PositionId(board);
            NotePosition(board, fromId);
            var pseudo = new List<ChessMove>(64);
            var legal = new List<ChessMove>(64);
            int ply = 0;
            foreach (var san in sans)
            {
                MoveGen.Legal(board, pseudo, legal);
                var mv = San.Resolve(board, legal, san);
                if (mv is null) throw new InvalidDataException($"{origin}: illegal SAN '{san}' at ply {ply + 1}.");
                Piece moving = board.Squares[mv.Value.From];
                var moveId = ChessCompose.MoveId(moving, mv.Value);
                var key = ChessCompose.TransitionKey(fromId, moveId);
                MoveApply.Make(board, mv.Value);
                var toId = ChessCompose.PositionId(board);
                if (transitions.TryGetValue(key, out var previous) && previous != toId)
                    throw new InvalidDataException("Seed transition has competing deterministic results.");
                transitions[key] = toId;
                NotePosition(board, toId);
                fromId = toId;
                ply++;
            }
        }

        // Standard start
        WalkLine(Array.Empty<string>(), m.Initial());
        surfaces.Add(m.StateKey(m.Initial()));

        for (int sp = 0; sp < Chess960Positions.Count; sp++)
        {
            string rank = Chess960Positions.BackRank(sp);
            string black = new string(rank.Select(char.ToLowerInvariant).ToArray());
            string fen = $"{black}/pppppppp/8/8/8/8/PPPPPPPP/{rank} w KQkq - 0 1";
            var st = m.FromFen(fen);
            surfaces.Add(m.StateKey(st));
            _ = ChessCompose.PositionId(st.Board);
        }

        if (!string.Equals(openings, "NONE", StringComparison.Ordinal)
            && Directory.Exists(openings))
        {
            int records = 0;
            foreach (var file in Directory.EnumerateFiles(openings, "*.tsv")
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                int lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (ChessOpeningsDecomposer.ParseRow(line, file, lineNumber) is not { } row) continue;
                    try
                    {
                        var sans = ChessOpeningsDecomposer.ExtractSans(row.Movetext);
                        if (sans.Count == 0) throw new InvalidDataException("Opening has no playable moves.");
                        WalkLine(sans, m.Initial(), $"{file}:{lineNumber} ({row.Eco} {row.Name})");
                        records++;
                    }
                    catch (Exception failure)
                    {
                        throw new InvalidDataException($"Opening catalog source failed at {file}:{lineNumber} ({row.Eco} {row.Name}).", failure);
                    }
                }
            }
            if (records == 0)
                throw new InvalidDataException($"Declared openings directory '{openings}' contains no opening records.");
        }
        else if (!string.Equals(openings, "NONE", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"missing openings: {openings}");
            return 2;
        }

        WriteAllLinesAtomic(surfacesOut, surfaces);

        var sorted = transitions
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(t => t.Key, Hash128BytewiseComparer.Instance)
            .ToList();
        ChessTransitionFloor.WriteBlob(transitionsOut, sorted);

        Console.Error.WriteLine(
            $"chess_catalog: {surfaces.Count} surfaces -> {surfacesOut}; "
            + $"{sorted.Count} transitions -> {transitionsOut}");
        return 0;
    }

    static void WriteAllLinesAtomic(string path, IEnumerable<string> lines)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot publish chess catalog surfaces to '{fullPath}'.", failure);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                Laplace.Ops.LaplaceLogging.ReportProcessFailure($"CHESS_CATALOG_CLEANUP_FAILED path={temporary}", failure);
            }
        }
    }

    static string Ep(Board b)
    {
        int ep = ChessModality.CapturableEpSquare(b);
        return ep < 0 ? "-" : Board.SquareToAlgebraic(ep);
    }

    sealed class Hash128BytewiseComparer : IComparer<Hash128>
    {
        public static readonly Hash128BytewiseComparer Instance = new();
        public int Compare(Hash128 x, Hash128 y) => x.CompareToBytewise(y);
    }
}
