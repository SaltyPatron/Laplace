using System.Diagnostics;

namespace Laplace.Chess.Service;

/// <summary>
/// Per-run tally of records a chess lane REFUSED, by reason.
///
/// WHY THIS EXISTS. Every drop site in the chess lanes called
/// <c>System.Diagnostics.Trace.TraceWarning</c> and moved on. No trace listener is
/// registered in the CLI, so those strings were written to nothing: a run that refused
/// a third of its input and a run that refused none produced byte-identical output, and
/// <c>INGEST_COMPLETE ... failed=0 status=ok</c> in both cases. "An unattested id is not
/// an id attested false" is the read-side rule; the same applies to the write side — a
/// record the parser could not read is a fact about the corpus, and a fact nobody counts
/// is a fact nobody can act on.
///
/// The ledger is a process-global counter set because the parse sites are static and run
/// across the file-worker pool. One ingest at a time is already the operating rule, so a
/// single set is the right grain; <see cref="Reset"/> at run start keeps repeated
/// in-process runs (tests, the MCP server) honest.
/// </summary>
internal static class ChessDropLedger
{
    internal const string UnreadableSan = "unresolvable-san";
    internal const string NoMovetext = "no-movetext";
    internal const string NoResultOrMoves = "no-result-or-moves";
    internal const string UnreadableStartPosition = "unreadable-start-position";

    /// <summary>
    /// A variant start position this modality does not model — in practice always
    /// Chess960, whose X-FEN/Shredder castling field carries rook FILES ("FCfc") rather
    /// than KQkq. <c>Board.FromFen</c> refuses it deliberately (replaying it from the
    /// standard array would record a game that was never played), and modelling it means
    /// keeping rook files on the board, which reaches <c>StateKey</c> and therefore the
    /// content id of every position — a substrate-wide identity change that is its own
    /// piece of work, not a side effect of a parser fix.
    ///
    /// Broken out from <see cref="UnreadableStartPosition"/> so the drop report
    /// distinguishes "a variant we chose not to model" from "a corrupt FEN", which are
    /// the same line of output but completely different decisions.
    /// </summary>
    internal const string UnmodelledVariant = "unmodelled-variant";
    internal const string PgnBlockWithoutResult = "pgn-block-without-result";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Counts = new();
    private static long _kept;

    internal static void Reset()
    {
        Counts.Clear();
        Interlocked.Exchange(ref _kept, 0);
    }

    internal static void Kept() => Interlocked.Increment(ref _kept);

    /// <summary>
    /// Count one refusal. <paramref name="detail"/> is kept for the first few occurrences
    /// of each reason only — a corpus with 400k unreadable games must not turn its own
    /// diagnosis into the memory problem.
    /// </summary>
    internal static void Drop(string reason, string? detail = null)
    {
        long n = Counts.AddOrUpdate(reason, 1, static (_, v) => v + 1);
        if (n <= SampleDetailsPerReason && detail is not null)
            Samples.AddOrUpdate(reason, [detail], (_, list) =>
            {
                lock (list) { if (list.Count < SampleDetailsPerReason) list.Add(detail); }
                return list;
            });
    }

    private const int SampleDetailsPerReason = 3;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> Samples = new();

    internal static long Total
    {
        get
        {
            long n = 0;
            foreach (var kv in Counts) n += kv.Value;
            return n;
        }
    }

    internal static long KeptCount => Interlocked.Read(ref _kept);

    /// <summary>
    /// One line naming what the lane refused and why, or null when it refused nothing.
    /// Emitted at the end of extraction so it lands next to INGEST_COMPLETE in the job log
    /// and in the on-disk detail log the CI workflows keep.
    /// </summary>
    internal static string? Summary(string lane)
    {
        long total = Total;
        if (total == 0) return null;
        long kept = KeptCount;
        long seen = kept + total;
        var parts = Counts.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}={kv.Value}");
        var line = $"CHESS_DROPPED source={lane} dropped={total} kept={kept} seen={seen} "
                 + $"drop_pct={(seen == 0 ? 0 : 100.0 * total / seen):F2} [{string.Join(' ', parts)}]";
        foreach (var kv in Samples.OrderBy(k => k.Key))
            foreach (var s in kv.Value)
                line += $"\n  e.g. [{kv.Key}] {s}";
        return line;
    }

    /// <summary>
    /// Write the summary where an operator will actually see it. Console, not Trace: the
    /// CLI registers no trace listener, which is the whole reason these drops were
    /// invisible. Progress lines already go to stdout, so this sits with them.
    /// </summary>
    internal static void Report(string lane)
    {
        if (Summary(lane) is not { } s) return;
        Console.Out.WriteLine(s);
        Trace.TraceWarning(s);
    }

    /// <summary>
    /// Why a text-corpus chess lane applied zero units (see
    /// <see cref="Laplace.Decomposers.Abstractions.IIngestNoOpExplainer"/>).
    ///
    /// Records were READ and none applied means the novelty gate proved every one already
    /// present — an idempotent re-ingest, the safe operation, which used to exit 1. Records
    /// read and every one dropped is a genuine format failure and stays a failure. Nothing
    /// read at all is unreachable now that <see cref="ChessInput"/> throws first, but it
    /// also stays a failure.
    /// </summary>
    internal static (string Status, string Detail)? ExplainEmptyRun(string lane, long declaredInputUnits)
    {
        long kept = KeptCount, dropped = Total;
        if (kept > 0)
            return ("already-present",
                $"{lane}: read {kept} record(s) of {declaredInputUnits} declared"
                + (dropped > 0 ? $" ({dropped} refused)" : "")
                + " and the novelty gate proved every one already in the substrate — "
                + "idempotent re-ingest, nothing to write.");
        return null;
    }
}
