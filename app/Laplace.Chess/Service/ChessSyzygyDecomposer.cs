using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Ingest Syzygy tablebase packaging through the generic multi-file spine.
///
/// Every physical <c>.rtbw</c>/<c>.rtbz</c> package file is scheduled independently so the
/// shared file-content fingerprint, completion marker, restart skip, journal counters and
/// bounded file-worker pool apply to the complete installed table set. Semantic position graph
/// expansion is a separate decision: only WDL (<c>.rtbw</c>) tables at or below the configured
/// exhaustive men ceiling are walked. Larger packages and DTZ files are still durable,
/// content-addressed ETL inputs and remain available to Fathom for exact lazy probing; they are
/// not discarded merely because brute-force placement enumeration would be absurd.
///
/// Run: <c>laplace ingest chess-syzygy [&lt;syzygy-dir&gt;]</c>
/// </summary>
public sealed class ChessSyzygyDecomposer
    : DecomposerMultiFile<ChessSyzygyRecord>, IIngestNoOpExplainer, IIngestInventoryProvider
{
    internal static readonly string[] PackageExtensions = [".rtbw", ".rtbz"];

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

    // One semantic record is a prepared leaf of up to 2,048 transitions, including the
    // reusable position/move physicalities referenced by its packed transition graph.
    // Package-only files emit no semantic records; their file boundary still carries the
    // generic content fingerprint/completion receipt.
    public override int EstimatedBytesPerRecord => 8 * 1_024 * 1_024;
    public override int EstimatedComposeUnitsPerRecord => 1;

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

        // Resolve the COMPLETE package set. The old path filtered by men BEFORE the generic
        // multi-file driver saw the files, so 4-7 man WDL files had no content fingerprint,
        // completion marker, journal row or resumable ETL identity; .rtbz was invisible entirely.
        try
        {
            var all = ChessInput.Resolve(
                _resolvedDir, SearchOption.TopDirectoryOnly,
                PackageExtensions, "chess-syzygy");
            int maxMen = SyzygyTableUnpack.ResolveMaxMen();
            LogPackagePlanOnce(all, maxMen);
            return SchedulePackages(all);
        }
        catch (ChessInputException) when (_packagingMissing || _initFailed)
        {
            return Array.Empty<(string, string)>();
        }
    }

    /// <summary>
    /// Stable package scheduler. Nothing is removed here. Labels include the extension because
    /// <c>KQvK.rtbw</c> and <c>KQvK.rtbz</c> are distinct physical package inputs and the generic
    /// multi-file journal requires unique labels.
    /// </summary>
    internal static IReadOnlyList<(string Path, string Label)> SchedulePackages(
        IReadOnlyList<string> paths) =>
        paths
            .OrderBy(p => SyzygyTableUnpack.ParseMen(Path.GetFileNameWithoutExtension(p)!))
            .ThenBy(p => Path.GetExtension(p).Equals(".rtbw", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p, StringComparer.Ordinal)
            .Select(p => (p, Path.GetFileName(p)))
            .ToArray();

    /// <summary>
    /// Whether this physical package should be exhaustively converted to the compact
    /// position→move→position material graph. Package scheduling/completion is independent of
    /// this predicate; returning false means "receipt/cache only", never "ignore the file".
    /// </summary>
    internal static bool ShouldExpandPackage(string path, int maxMen)
    {
        if (!Path.GetExtension(path).Equals(".rtbw", StringComparison.OrdinalIgnoreCase))
            return false;
        string material = Path.GetFileNameWithoutExtension(path)!;
        return SyzygyTableUnpack.ParseMen(material) <= maxMen;
    }

    /// <summary>
    /// Semantic-expansion subset retained for tests/diagnostics. Unlike the old implementation,
    /// this is NOT the set passed to the multi-file scheduler.
    /// </summary>
    internal static IReadOnlyList<string> FilterByMenCeiling(
        IReadOnlyList<string> paths, int maxMen)
    {
        var kept = new List<string>(paths.Count);
        foreach (var p in paths)
            if (ShouldExpandPackage(p, maxMen))
                kept.Add(p);
        return kept;
    }

    private void LogPackagePlanOnce(IReadOnlyList<string> all, int maxMen)
    {
        if (_ceilingLogged) return;
        _ceilingLogged = true;
        int wdl = all.Count(static p =>
            Path.GetExtension(p).Equals(".rtbw", StringComparison.OrdinalIgnoreCase));
        int dtz = all.Count - wdl;
        int expanded = all.Count(p => ShouldExpandPackage(p, maxMen));
        int lazyWdl = wdl - expanded;
        Console.Error.WriteLine(
            $"chess-syzygy: package-files={all.Count} wdl={wdl} dtz={dtz}; "
            + $"semantic-expand={expanded} wdl table(s) <= {maxMen} men; "
            + $"lazy-package-only={lazyWdl + dtz}. Every package is content-fingerprinted and "
            + "file-resumable; larger WDL/DTZ remain available to Fathom without exhaustive expansion.");
    }

    protected override async IAsyncEnumerable<ChessSyzygyRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_prober is null) yield break;

        string material = Path.GetFileNameWithoutExtension(filePath)!;
        if (!SyzygyTableUnpack.TryParseMaterial(material, out _))
            throw new InvalidDataException(
                $"Syzygy package '{fileLabel}' does not have a parseable material basename.");

        int maxMen = SyzygyTableUnpack.ResolveMaxMen();
        if (!ShouldExpandPackage(filePath, maxMen))
        {
            // Deliberately no semantic row. The generic multi-file boundary still fingerprints
            // the exact bytes and emits HasLayerCompleted on that content identity, so this file
            // is independently receipted and true-skipped on restart without boiling its state
            // space into rows. Fathom consumes these mapped files lazily during search/game probes.
            yield break;
        }

        // The outer file workers already run materials in parallel, so a fixed inner fan
        // would multiply into fileWorkers × workers threads. Every material asks for the full
        // compose-worker fan while all concurrent materials share one process-wide probe budget.
        var probeBudget = _probeBudget;
        int workers = probeBudget is null
            ? 1
            : Math.Max(1, IngestTopology.Current.ComposeWorkers);

        // Stream semantic chunks. The prior implementation retained every FEN string for the
        // material and only composed after the final probe, ballooning resident memory.
        var products = new List<SyzygyProduct>(ChessSyzygy.TransitionsPerChunk);
        var chunks = new List<SyzygyChunkRef>();
        long cap = options.MaxInputUnits;
        long decoded = 0;
        await foreach (var product in SyzygyTableUnpack.ExtractMaterialAsync(
                           material, _prober, workers, probeBudget, ct).ConfigureAwait(false))
        {
            products.Add(product);
            decoded++;
            if (products.Count == ChessSyzygy.TransitionsPerChunk)
            {
                var record = ChessSyzygyRecord.CreateChunk(material, products);
                if (record.Chunk is { } chunk && chunk.Id != default)
                {
                    chunks.Add(chunk);
                    yield return record;
                }
                products = new List<SyzygyProduct>(ChessSyzygy.TransitionsPerChunk);
            }
            if (cap > 0 && decoded >= cap) break;
        }
        if (products.Count > 0)
        {
            var record = ChessSyzygyRecord.CreateChunk(material, products);
            if (record.Chunk is { } chunk && chunk.Id != default)
            {
                chunks.Add(chunk);
                yield return record;
            }
        }

        // An eligible WDL table that produced no exact root transitions is not a successful
        // package ingest. Usually that means its DTZ partner/package is absent or Fathom cannot
        // answer the loaded material. Fail the file so no completion marker can hide it.
        if (chunks.Count == 0)
            throw new InvalidDataException(
                $"Syzygy WDL package '{fileLabel}' was eligible for semantic expansion but "
                + "produced zero probeable transitions; file remains incomplete for retry.");

        yield return ChessSyzygyRecord.CreateMaterialRoot(material, chunks);
    }

    protected override IIngestRecordHandler<ChessSyzygyRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new DirectComposeHandler<ChessSyzygyRecord>(static (r, b) =>
        {
            if (r.PreparedChunk is { } chunk)
                ChessSyzygy.DeriveTransitionChunk(b, chunk);
            else if (r.Chunks is { } chunks)
                ChessSyzygy.DeriveMaterialRoot(b, chunks, r.TrunkRootId);
            else if (r.Product is { } product)
                ChessSyzygy.DeriveProduct(b, product);
        }, unitsPerRecord: static r => r.InputUnits);

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
        // Semantic input units count decoded positions, not package-file completion receipts.
        // A package-only run can therefore have zero decoded units while still completing real
        // per-file ETL work. ExplainEmptyDirectory distinguishes missing packaging from deliberate
        // package-only semantic scoping.
        if (declaredInputUnits == 0 && _resolvedDir is not null)
            return ExplainEmptyDirectory(_resolvedDir, SyzygyTableUnpack.ResolveMaxMen());
        return null;
    }

    /// <summary>
    /// Empty semantic-run triage. No packages at all is dependency-unset. A directory containing
    /// only WDL tables above the exhaustive ceiling remains `scoped-out` for semantic expansion
    /// (the files themselves are still fingerprinted/completed by the multi-file lane). Any DTZ
    /// package or any expandable WDL means a zero-record run is unexpected and remains unexplained.
    /// </summary>
    internal static (string Status, string Detail)? ExplainEmptyDirectory(
        string resolvedDir, int maxMen)
    {
        IReadOnlyList<string> all;
        try
        {
            all = ChessInput.Resolve(
                resolvedDir, SearchOption.TopDirectoryOnly,
                PackageExtensions, "chess-syzygy");
        }
        catch (ChessInputException)
        {
            all = Array.Empty<string>();
        }
        if (all.Count == 0)
            return ("dependency-unset",
                "ChessSyzygy: packaging directory resolved but contained no .rtbw/.rtbz files.");

        bool hasDtz = all.Any(static p =>
            Path.GetExtension(p).Equals(".rtbz", StringComparison.OrdinalIgnoreCase));
        if (!hasDtz && FilterByMenCeiling(all, maxMen).Count == 0)
            return ("scoped-out",
                $"ChessSyzygy: all {all.Count} WDL table(s) exceed the {maxMen}-men "
                + "full-enumeration ceiling (LAPLACE_SYZYGY_MAX_MEN). Package-byte receipts "
                + "still complete file-by-file; no position graph was exhaustively expanded.");
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
    /// Coarse estimate only — the runner consults this when <see cref="DescribeInputAsync"/>
    /// yields no inventory (dir unresolved / no packages), never as decoded-position progress.
    /// Every package file counts here even when its state space is intentionally not expanded.
    /// </summary>
    public override Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        var dir = ChessInput.ResolveSyzygyPackagingDir(context.EcosystemPath);
        if (dir is null) return Task.FromResult<long?>(null);
        long n = ListFiles(dir, DecomposerOptions.Default).Count;
        return Task.FromResult<long?>(n == 0 ? null : n);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var dir = ChessInput.ResolveSyzygyPackagingDir(context.EcosystemPath);
        if (dir is null)
            return Task.FromResult<IngestInventory?>(null);
        var paths = ListFiles(dir, options).Select(t => t.Path).ToList();
        // Decoded positions are unknown without walking eligible WDL material. File inventory is
        // nevertheless exact across WDL+DTZ and drives true per-file completion/resume.
        return Task.FromResult(IngestInventory.FromFilesWithUnknownUnitCount(
            "positions", paths, options.MaxInputUnits, tracksFileCompletion: true));
    }
}

/// <summary>
/// One streamed transition chunk, its material root, or a legacy single-position test record.
/// Physical package receipts live at the generic multi-file boundary, not in this semantic record.
/// </summary>
public sealed record ChessSyzygyRecord : ITrunkRootRecord
{
    public ChessSyzygyRecord(SyzygyProduct product)
    {
        Product = product;
        TrunkRootId = ChessSyzygy.MarkerId(product.PositionId, ChessSyzygy.Version);
        InputUnits = 1;
    }

    private ChessSyzygyRecord(
        string material, SyzygyTransitionChunk? preparedChunk,
        IReadOnlyList<SyzygyChunkRef>? chunks,
        Hash128 trunkRootId, long inputUnits)
    {
        Material = material;
        PreparedChunk = preparedChunk;
        Chunks = chunks;
        TrunkRootId = trunkRootId;
        InputUnits = inputUnits;
    }

    public static ChessSyzygyRecord CreateChunk(string material, IReadOnlyList<SyzygyProduct> products)
    {
        var chunk = ChessSyzygy.PrepareTransitionChunk(products)
                    ?? throw new InvalidDataException("Syzygy transition chunk is empty");
        return new ChessSyzygyRecord(
            material, chunk, null, chunk.Id, products.Count);
    }

    public static ChessSyzygyRecord CreateMaterialRoot(
        string material, IReadOnlyList<SyzygyChunkRef> chunks) =>
        new(material, null, chunks, ChessSyzygy.MaterialId(material), 0);

    public SyzygyProduct? Product { get; }
    public string? Material { get; }
    public SyzygyTransitionChunk? PreparedChunk { get; }
    public SyzygyChunkRef? Chunk => PreparedChunk?.Reference;
    public IReadOnlyList<SyzygyChunkRef>? Chunks { get; }
    public Hash128 TrunkRootId { get; }
    public long InputUnits { get; }
}