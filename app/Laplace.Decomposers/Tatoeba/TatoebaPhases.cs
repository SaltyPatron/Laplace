using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Tatoeba;

/// <summary>
/// One Tatoeba file per phase: sentences.csv completes, then links.csv. The id → root map
/// links resolve through is recorded by <see cref="TatoebaEmitter.Emit"/> as it composes
/// each sentence root, so no separate pass over sentences.csv is needed. Each phase is a
/// single file, so <c>Decomposer&lt;TRecord&gt;.RunDecomposeAsync</c> routes it through
/// MonolithSegmenter and it keeps intra-file parallelism.
/// </summary>
internal abstract class TatoebaPhase : DecomposerPhase<TatoebaIngestRecord>
{
    private readonly TatoebaIdMap _ids;
    private readonly ConcurrentDictionary<long, byte>? _allowedIds;
    private readonly string _fileName;

    protected TatoebaPhase(
        TatoebaIdMap ids, ConcurrentDictionary<long, byte>? allowedIds, string fileName)
    {
        _ids = ids;
        _allowedIds = allowedIds;
        _fileName = fileName;
    }

    protected abstract TatoebaRowKind Kind { get; }

    protected virtual Func<ReadOnlySpan<byte>, bool>? AcceptRow(DecomposerOptions options) => null;

    public override Hash128 SourceId => TatoebaSource.SourceId;
    public override string SourceName => "TatoebaDecomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TatoebaSource.TrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    protected override IIngestRecordHandler<TatoebaIngestRecord> CreateHandler()
    {
        var emitter = new TatoebaEmitter(Kind, _allowedIds, _ids);
        return new DirectComposeHandler<TatoebaIngestRecord>(emitter.Emit);
    }

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.Compose(
            SourceId, BatchLabelPrefix, options, context.Reader, IngestSourceProfile.Tatoeba);

    protected override async IAsyncEnumerable<TatoebaIngestRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string path = Path.Combine(ecosystemPath, _fileName);
        if (!File.Exists(path)) yield break;

        var accept = AcceptRow(options);
        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (line.IsEmpty || (accept is not null && !accept(line.Span))) continue;
            TatoebaIngestRecord record;
            bool parsed = Kind == TatoebaRowKind.Sentence
                ? TatoebaParse.TrySentence(line.Span, out record)
                : TatoebaParse.TryLink(line.Span, out record);
            if (parsed) yield return record;
        }
    }
}

/// <summary>sentences.csv: composes the sentence content roots and records id → root.</summary>
internal sealed class TatoebaSentencePhase : TatoebaPhase
{
    public TatoebaSentencePhase(TatoebaIdMap ids, ConcurrentDictionary<long, byte>? allowedIds)
        : base(ids, allowedIds, "sentences.csv") { }

    protected override string PhaseLabel => "sent";
    protected override TatoebaRowKind Kind => TatoebaRowKind.Sentence;

    protected override Func<ReadOnlySpan<byte>, bool>? AcceptRow(DecomposerOptions options) =>
        options.Languages is { IsActive: true } langs
            ? line => TatoebaRowFilter.MatchesSentenceLanguageFilter(line, langs)
            : null;
}

/// <summary>links.csv: attestations only, between roots the sentence file already composed.</summary>
internal sealed class TatoebaLinkPhase : TatoebaPhase
{
    private readonly ConcurrentDictionary<long, byte>? _allowedIds;

    public TatoebaLinkPhase(TatoebaIdMap ids, ConcurrentDictionary<long, byte>? allowedIds)
        : base(ids, allowedIds, "links.csv") => _allowedIds = allowedIds;

    protected override string PhaseLabel => "link";
    protected override TatoebaRowKind Kind => TatoebaRowKind.Link;

    protected override Func<ReadOnlySpan<byte>, bool>? AcceptRow(DecomposerOptions options) =>
        _allowedIds is not null
            ? line => TatoebaRowFilter.MatchesLinkFilter(line, _allowedIds)
            : null;
}
