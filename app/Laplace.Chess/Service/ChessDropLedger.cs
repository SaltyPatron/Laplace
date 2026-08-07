using System.Diagnostics;
using System.Globalization;
using System.Text;
using Laplace.Core;

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
    /// A variant start position this modality does not model.
    ///
    /// Chess960 USED to be counted here and no longer is — it is modelled (see
    /// <see cref="Laplace.Modality.Chess.Chess960Positions"/>). The reasoning that kept it
    /// out was wrong and is worth recording: modelling it was said to reach
    /// <c>StateKey</c> and therefore the content id of every position, i.e. a reseed. It
    /// does not. Identity embeds <c>Board.CastleString()</c>, the rook files default to the
    /// standard ones, and CastleString emits the classic KQkq whenever they hold — so an
    /// ordinary position's surface is byte-identical and only boards whose rooks are NOT on
    /// a/h get new ids. Those are exactly the boards the substrate did not contain, because
    /// the games were being refused. Additive, not a reseed.
    ///
    /// Kept as a distinct reason for the next variant along, and because the report should
    /// separate "a variant we do not model" from "a corrupt FEN" — the same line of output,
    /// completely different decisions.
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

    internal static SortedDictionary<string, long> SnapshotCounts()
    {
        var d = new SortedDictionary<string, long>();
        foreach (var kv in Counts) d[kv.Key] = kv.Value;
        return d;
    }

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
        AppendOpsCsv(lane);
    }

    /// <summary>
    /// Persist per-reason tallies for <c>ops.chess_drops()</c> (GH #813). One CSV
    /// row per reason so the drop profile is queryable without grepping job logs.
    /// Best-effort: a ledger write must never fail the ingest.
    /// </summary>
    private static void AppendOpsCsv(string lane)
    {
        try
        {
            long kept = KeptCount;
            long total = Total;
            long seen = kept + total;
            double dropPct = seen == 0 ? 0.0 : 100.0 * total / seen;
            var dir = LaplaceInstall.OpsLogDirectory;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "laplace-chess-drops.csv");
            var needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var w = new StreamWriter(fs, Encoding.UTF8);
            if (needHeader)
                w.WriteLine("log_time,source_name,reason,dropped,kept,seen,drop_pct");
            static string Csv(string s) =>
                "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            var when = DateTimeOffset.UtcNow.ToString("o");
            foreach (var kv in SnapshotCounts())
            {
                w.WriteLine(string.Join(',',
                    when,
                    Csv(lane),
                    Csv(kv.Key),
                    kv.Value.ToString(CultureInfo.InvariantCulture),
                    kept.ToString(CultureInfo.InvariantCulture),
                    seen.ToString(CultureInfo.InvariantCulture),
                    dropPct.ToString("F2", CultureInfo.InvariantCulture)));
            }
        }
        catch
        {
            // Ops ledger is diagnostic; stdout CHESS_DROPPED remains the primary signal.
        }
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

/// <summary>Read-only view of the ledger for the corpus drop-profile probe.</summary>
internal static class ChessDropLedgerProbe
{
    internal static void Reset() => ChessDropLedger.Reset();

    internal static (long Kept, SortedDictionary<string, long> Reasons) Snapshot()
        => (ChessDropLedger.KeptCount, ChessDropLedger.SnapshotCounts());
}
