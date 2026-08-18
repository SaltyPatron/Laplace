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
/// <c>tb_probe_wdl</c>). Path resolution is <see cref="ChessInput.ResolveSyzygyPackagingDir"/>.
/// Run: <c>laplace ingest chess-syzygy [&lt;syzygy-dir&gt;]</c>
/// </summary>
public sealed class ChessSyzygyDecomposer
    : DecomposerMultiFile<ChessSyzygyRecord>, IIngestNoOpExplainer, IIngestInventoryProvider
{
    private readonly Func<ISyzygyProber>? _proberFactory;
    private ISyzygyProber? _prober;
    private bool _packagingMissing;
    private bool _initFailed;
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
    protected override int DefaultBatchSize => BatchConfigDefaults.Chess;

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessSyzygy.SourceId, SourceName, ChessSyzygy.TrustClassId, ct);
        _packagingMissing = false;
        _initFailed = false;
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
            return ChessInput.Resolve(
                    _resolvedDir, SearchOption.TopDirectoryOnly,
                    ChessInput.SyzygyExtensions, "chess-syzygy")
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

    protected override async IAsyncEnumerable<ChessSyzygyRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_prober is null) yield break;
        long cap = options.MaxInputUnits;
        long n = 0;
        await foreach (var product in SyzygyTableUnpack.ExtractMaterialAsync(
                           fileLabel, _prober, workers: 1, ct).ConfigureAwait(false))
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
            SourceId, $"{BatchLabelPrefix}/{fileLabel}", DefaultBatchSize, options, reader, PipelineProfile);

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
    {
        if (_packagingMissing)
            return ("dependency-unset",
                "ChessSyzygy: no tablebase packaging directory (path, LAPLACE_SYZYGY, or "
                + "data-root Games/Chess/syzygy/…) — unpack is a documented no-op.");
        if (_initFailed)
            return ("dependency-unset",
                $"ChessSyzygy: Fathom found no tables under {_resolvedDir} — unpack no-op.");
        if (declaredInputUnits == 0)
            return ("dependency-unset",
                "ChessSyzygy: packaging directory resolved but contained no .rtbw files.");
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
        return Task.FromResult(IngestInventory.FromFileUnits(
            "tables", paths, options.MaxInputUnits, tracksFileCompletion: true));
    }
}

/// <summary>
/// One unpacked board-state product; trunk root is the versioned per-POSITION marker.
/// </summary>
public sealed record ChessSyzygyRecord(SyzygyProduct Product) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessSyzygy.MarkerId(Product.PositionId, ChessSyzygy.Version);
}
