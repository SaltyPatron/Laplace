using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Ingest Syzygy tablebases as substrate records via the multi-file spine.
/// Each <c>.rtbw</c> is one packaging unit (material table) — file workers unpack
/// materials in parallel; within a material, WDL probes fan out (thread-safe Fathom
/// <c>tb_probe_wdl</c>) across ONE ComposeWorkers-sized budget shared by all
/// concurrently-unpacking materials. Enumeration is scoped to materials at or under
/// the men ceiling (<see cref="SyzygyTableUnpack.DefaultMaxMen"/> /
/// <c>LAPLACE_SYZYGY_MAX_MEN</c>); larger materials are seeded by the game-driven
/// probe path. Path resolution is <see cref="ChessInput.ResolveSyzygyPackagingDir"/>.
/// Run: <c>laplace ingest chess-syzygy [&lt;syzygy-dir&gt;]</c>
/// </summary>
public sealed class ChessSyzygyDecomposer
    : DecomposerMultiFile<ChessSyzygyRecord>, IIngestNoOpExplainer, IIngestInventoryProvider
{
    private readonly Func<ISyzygyProber>? _proberFactory;
    private ISyzygyProber? _prober;
    private SemaphoreSlim? _probeBudget;
    private bool _packagingMissing;
    private bool _initFailed;
    private bool _ceilingLogged;
    private string? _resolvedDir;

    public ChessSyzygyDecomposer(Func<ISyzygyProber>? proberFactory = null)
        => _proberFactory = proberFactory;

    public override Hash128 SourceId => ChessSyzygy.SourceId;
    public override string SourceName => ChessSyzygy.SourceName;
    public override int LayerOrder => 23;
    public override Hash128 TrustClassId => ChessSyzygy.TrustClassId;
    public override bool PerFileCompletion => true;
    protected override double SourceTrust => TC.StandardsDerived;
    protected override string BatchLabelPrefix => "chess/syzygy";

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessSyzygy.SourceId, SourceName, ChessSyzygy.TrustClassId, ct);
        _probeBudget ??= new SemaphoreSlim(Math.Max(1, IngestTopology.Current.ComposeWorkers));
        _packagingMissing = false;
        _initFailed = false;
        _ceilingLogged = false;
        _resolvedDir = ChessInput.ResolveSyzygyPackagingDir(context.EcosystemPath);
        if (_resolvedDir is null)
        {
            _packagingMissing = true;
            return;
        }

        if (_proberFactory is not null)
        {
            _prober = _proberFactory();
            return;
        }

        int largest = SyzygyNative.Init(_resolvedDir);
        if (largest <= 0)
        {
            _initFailed = true;
            System.Diagnostics.Trace.TraceWarning(
                "ChessSyzygy: no tables discovered under {0} (init={1})", _resolvedDir, largest);
            return;
        }

        _prober = new SyzygyNativeProber();
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        _resolvedDir ??= ChessInput.ResolveSyzygyPackagingDir(ecosystemPath);
        if (_resolvedDir is null || _prober is null)
            return Array.Empty<(string, string)>();

        // Prefer ChessInput.Resolve so empty/mis-pointed dirs fail loudly when tables
        // were expected; packaging-missing no-op uses the empty list + ExplainEmptyRun.
        try
        {
            var all = ChessInput.Resolve(
                _resolvedDir, SearchOption.TopDirectoryOnly,
                ChessInput.SyzygyExtensions, "chess-syzygy");
            // Full enumeration is scoped by the men ceiling BEFORE anything is
            // declared, so file totals, per-file completion and progress stay honest.
            // Rationale on SyzygyTableUnpack.DefaultMaxMen: exhaustive unpack is only
            // viable at 3-men scale (~500k products/table); 4-men is ~10^9 products
            // and 5-men ~10^11 — larger materials are seeded by the game-driven path
            // (ChessSyzygy.DeriveGame), not by exhaustive unpack.
            int maxMen = SyzygyTableUnpack.ResolveMaxMen();
            var kept = FilterByMenCeiling(all, maxMen);
            LogCeilingOnce(skipped: all.Count - kept.Count, total: all.Count, maxMen);
            return kept
                .OrderBy(p => SyzygyTableUnpack.ParseMen(
                    Path.GetFileNameWithoutExtension(p)!))
                .ThenBy(p => p, StringComparer.Ordinal)
                .Select(p => (p, Path.GetFileNameWithoutExtension(p)!))
                .ToArray();
        }
        catch (ChessInputException) when (_packagingMissing || _initFailed)
        {
            return Array.Empty<(string, string)>();
        }
    }

    /// <summary>
    /// Tables at or under the full-enumeration men ceiling. Unparseable basenames
    /// (<see cref="SyzygyTableUnpack.ParseMen"/> = MaxValue) never pass — they could
    /// not be unpacked anyway (TryParseMaterial gates the walk).
    /// </summary>
    internal static IReadOnlyList<string> FilterByMenCeiling(
        IReadOnlyList<string> paths, int maxMen)
    {
        var kept = new List<string>(paths.Count);
        foreach (var p in paths)
            if (SyzygyTableUnpack.ParseMen(Path.GetFileNameWithoutExtension(p)!) <= maxMen)
                kept.Add(p);
        return kept;
    }

    private void LogCeilingOnce(int skipped, int total, int maxMen)
    {
        if (skipped <= 0 || _ceilingLogged) return;
        _ceilingLogged = true;
        Console.Error.WriteLine(
            $"chess-syzygy: enumerating {total - skipped} of {total} table(s); {skipped} exceed "
            + $"the {maxMen}-men full-enumeration ceiling (raise via LAPLACE_SYZYGY_MAX_MEN; "
            + "larger materials are seeded by the game-driven probe path, not exhaustive unpack).");
    }

    protected override async IAsyncEnumerable<ChessSyzygyRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_prober is null) yield break;
        // The outer file workers already run materials in parallel, so a fixed inner fan
        // would multiply into fileWorkers × workers threads. Instead every material asks
        // for the FULL compose-worker fan and all concurrent materials share ONE
        // ComposeWorkers-sized probe budget: total in-flight probes never exceed the
        // budget at full file fan, and the tail of a run (few tables still unpacking)
        // automatically widens into the slots the finished tables released. Without the
        // budget (InitializeAsync not run), fall back to the serial fan.
        var probeBudget = _probeBudget;
        int workers = probeBudget is null
            ? 1
            : Math.Max(1, IngestTopology.Current.ComposeWorkers);
        long cap = options.MaxInputUnits;
        long n = 0;
        await foreach (var product in SyzygyTableUnpack.ExtractMaterialAsync(
                           fileLabel, _prober, workers, probeBudget, ct).ConfigureAwait(false))
        {
            yield return new ChessSyzygyRecord(product);
            if (cap > 0 && ++n >= cap) yield break;
        }
    }

    protected override IIngestRecordHandler<ChessSyzygyRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new DirectComposeHandler<ChessSyzygyRecord>(static (r, b) => ChessSyzygy.DeriveProduct(b, r.Product));

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        IngestPipelineDefaults.Compose(
            SourceId, $"{BatchLabelPrefix}/{fileLabel}", options, reader, PipelineProfile);

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
    {
        if (_packagingMissing)
            return ("dependency-unset",
                "ChessSyzygy: no tablebase packaging directory (path, LAPLACE_SYZYGY, or "
                + "data-root Games/Chess/syzygy/…) — unpack is a documented no-op.");
        if (_initFailed)
            return ("dependency-unset",
                $"ChessSyzygy: Fathom found no tables under {_resolvedDir} — unpack no-op.");
        // The unknown-unit inventory declares zero input units even when tables exist,
        // so declaredInputUnits == 0 no longer means "no files" — triage the directory
        // itself.
        if (declaredInputUnits == 0 && _resolvedDir is not null)
            return ExplainEmptyDirectory(_resolvedDir, SyzygyTableUnpack.ResolveMaxMen());
        return null;
    }

    /// <summary>
    /// Empty-run triage for a RESOLVED packaging directory: no tables at all is the
    /// documented dependency no-op; tables present but every one above the men ceiling
    /// is deliberate scoping and names the knob that widens it; tables under the
    /// ceiling with zero records applied stays UNexplained — a real anomaly must fail
    /// the run, not read as dependency-unset.
    /// </summary>
    internal static (string Status, string Detail)? ExplainEmptyDirectory(
        string resolvedDir, int maxMen)
    {
        IReadOnlyList<string> all;
        try
        {
            all = ChessInput.Resolve(
                resolvedDir, SearchOption.TopDirectoryOnly,
                ChessInput.SyzygyExtensions, "chess-syzygy");
        }
        catch (ChessInputException)
        {
            all = Array.Empty<string>();
        }
        if (all.Count == 0)
            return ("dependency-unset",
                "ChessSyzygy: packaging directory resolved but contained no .rtbw files.");
        if (FilterByMenCeiling(all, maxMen).Count == 0)
            return ("scoped-out",
                $"ChessSyzygy: all {all.Count} table(s) exceed the {maxMen}-men "
                + "full-enumeration ceiling (LAPLACE_SYZYGY_MAX_MEN) — nothing to unpack; "
                + "larger materials are seeded by the game-driven probe path.");
        return null;
    }

    /// <summary>Test hook: packaging + prober resolvable.</summary>
    internal bool TryLoadProber(out ISyzygyProber prober)
    {
        prober = default!;
        var dir = ChessInput.ResolveSyzygyPackagingDir("");
        if (dir is null) return false;
        if (_proberFactory is not null)
        {
            prober = _proberFactory();
            return true;
        }
        if (SyzygyNative.Init(dir) <= 0) return false;
        prober = new SyzygyNativeProber();
        return true;
    }

    /// <summary>
    /// Coarse fallback only — the runner consults this when <see cref="DescribeInputAsync"/>
    /// yields no inventory (dir unresolved / no tables), never as the progress denominator.
    /// File count, like FrameNet: the record total is unknowable without a full unpack.
    /// </summary>
    public override Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        var dir = ChessInput.ResolveSyzygyPackagingDir(context.EcosystemPath);
        if (dir is null) return Task.FromResult<long?>(null);
        long n = ListFiles(dir, DecomposerOptions.Default).Count;
        if (DecomposerOptions.Default.MaxInputUnits > 0)
            n = Math.Min(n, DecomposerOptions.Default.MaxInputUnits);
        return Task.FromResult<long?>(n == 0 ? null : n);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var dir = ChessInput.ResolveSyzygyPackagingDir(context.EcosystemPath);
        if (dir is null)
            return Task.FromResult<IngestInventory?>(null);
        var paths = ListFiles(dir, options).Select(t => t.Path).ToList();
        // One .rtbw is one material TABLE, but extraction consumes one input unit per
        // probed board state — millions per table. FromFileUnits declared the table
        // count (~145) as the unit total while every record consumed a unit, so
        // input_pct read 1,367,466%. The record total is unknowable without walking
        // the material's whole index space, so keep record-grain progress against a
        // deliberately unknown denominator and exact per-file completion — the same
        // contract FrameNet uses for its one-file-many-records lane.
        return Task.FromResult(IngestInventory.FromFilesWithUnknownUnitCount(
            "positions", paths, options.MaxInputUnits, tracksFileCompletion: true));
    }
}

/// <summary>
/// One unpacked board-state product; trunk root is the versioned per-POSITION marker.
/// </summary>
public sealed record ChessSyzygyRecord(SyzygyProduct Product) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessSyzygy.MarkerId(Product.PositionId, ChessSyzygy.Version);
}
