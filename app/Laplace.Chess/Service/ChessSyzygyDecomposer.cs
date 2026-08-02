using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

// CALCULATED Syzygy probe pass (campaign PR-8): scan witnessed LINES (GH #736 —
// distinct PLAYS_LINE objects) lacking the ChessSyzygy marker, hydrate via content
// roundtrip, replay, probe every ≤N-men position against the tablebase set under
// LAPLACE_SYZYGY (native Fathom kernel — an in-process mmap lookup, never a
// subprocess), attest HAS_WDL + HAS_DTZ under the ChessSyzygy source.
// Run: `laplace ingest chess-syzygy`  (no path — substrate is the source)
public sealed class ChessSyzygyDecomposer : ComposeDecomposer<ChessSyzygyRecord>, IIngestNoOpExplainer
{
    private readonly Func<ISyzygyProber>? _proberFactory;
    private ISyzygyProber? _prober;

    // Why the last extraction produced nothing, so a zero-apply run can be told apart from
    // a broken one. Both of these used to exit 1: EstimateUnitCountAsync declares every
    // RECORDED line as the denominator while the stream yields only lines still missing
    // this pass's marker, so a caught-up backfill always applied zero — and so did the
    // documented "no tablebase directory" no-op.
    private bool _proberUnavailable;
    private long _candidatesStreamed;

    /// <summary>proberFactory overrides the native kernel for tests; the default
    /// initializes the process-global Fathom prober against <see cref="ChessLabPaths.SyzygyDir"/>.</summary>
    public ChessSyzygyDecomposer(Func<ISyzygyProber>? proberFactory = null)
        => _proberFactory = proberFactory;

    public override Hash128 SourceId => ChessSyzygy.SourceId;
    public override string SourceName => ChessSyzygy.SourceName;
    public override int LayerOrder => 23;
    public override Hash128 TrustClassId => ChessSyzygy.TrustClassId;
    protected override double SourceTrust => TC.StandardsDerived;
    protected override string BatchLabelPrefix => "chess/syzygy";
    protected override int DefaultBatchSize => BatchConfigDefaults.Chess;

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessSyzygy.SourceId, SourceName, ChessSyzygy.TrustClassId, ct);

    protected override async IAsyncEnumerable<ChessSyzygyRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessSyzygy requires a live Postgres substrate (NpgsqlSubstrateReader). "
                + "Record games first: laplace ingest chess <pgn>");

        _proberUnavailable = false;
        _candidatesStreamed = 0;

        if (!TryLoadProber(out var prober))
        {
            _proberUnavailable = true;
            yield break; // clean no-op — ExplainEmptyRun accounts for it
        }

        _prober = prober;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options, DefaultBatchSize);
        // LINE-grain stream (GH #736): a verdict is a pure function of the position, so
        // a line shared by many playings is probed ONCE.
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedLinesAsync(
                           ds, ContainmentReader!, ws.Batch,
                           lineId => ChessSyzygy.MarkerId(lineId, ChessSyzygy.Version), ct))
        {
            _candidatesStreamed++;
            yield return new ChessSyzygyRecord(witnessed);
        }
    }

    /// <summary>
    /// A zero-apply syzygy run is expected in two documented cases, and was a hard failure
    /// in both. Anything else (candidates streamed, none applied) still fails.
    /// </summary>
    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
    {
        if (_proberUnavailable)
            return ("dependency-unset",
                "ChessSyzygy: no tablebase directory (env LAPLACE_SYZYGY or chess-lab.env) — "
                + "probe pass is a documented no-op. Unattested is not attested-false.");
        if (_candidatesStreamed == 0)
            return ("already-complete",
                $"ChessSyzygy: every one of {declaredInputUnits} recorded line(s) already "
                + $"carries the v{ChessSyzygy.Version} probe marker — nothing left to probe.");
        return null;
    }

    // Missing tablebase dir (or a dir holding no tables) is UNSET, not an error: the
    // lane no-ops with a counted warning so `seed-everything` style runs stay green on
    // boxes that never downloaded the ~1 GB set (unattested != attested-false).
    internal bool TryLoadProber(out ISyzygyProber prober)
    {
        if (_proberFactory is not null)
        {
            prober = _proberFactory();
            return true;
        }

        prober = default!;
        var dir = ChessLabPaths.SyzygyDir;
        if (!dir.Found)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ChessSyzygy: tablebase directory not found (env LAPLACE_SYZYGY or "
                + "chess-lab.env; probed {0}) — probe pass is a no-op", dir.Path ?? "<unset>");
            return false;
        }

        int largest = SyzygyNative.Init(dir.Path!);
        if (largest <= 0)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ChessSyzygy: no tables discovered under {0} (init={1}) — "
                + "probe pass is a no-op", dir.Path, largest);
            return false;
        }

        prober = new SyzygyNativeProber();
        return true;
    }

    protected override void Compose(ChessSyzygyRecord record, SubstrateChangeBuilder b)
        => ChessSyzygy.DeriveGame(b, record.Game, _prober!);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedLinesAsync(ds, ct);
    }
}

/// <summary>
/// Syzygy pipeline record; trunk root is the versioned per-LINE marker so re-runs
/// dedup against the marker, never against the line.
/// </summary>
public sealed record ChessSyzygyRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessSyzygy.MarkerId(Game.LineId, ChessSyzygy.Version);
}
