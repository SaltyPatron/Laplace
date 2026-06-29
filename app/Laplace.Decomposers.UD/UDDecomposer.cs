using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.UD;

public sealed class UDDecomposer : IDecomposer, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/UDDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "UDDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => new List<string>(_canonicalNames.Keys);

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("TRANSCRIBES_AS");
        boot.AddRelationType("ENHANCED_DEPENDS_ON");
        boot.AddRelationType("HAS_XPOS");
        boot.AddRelationType("HAS_LANGUAGE");
        boot.AddType("UD_Feature");
        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            _canonicalNames.TryAdd(n, 0);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string treebanksDir = Path.Combine(context.EcosystemPath, "ud-treebanks-v2.17");
        if (!Directory.Exists(treebanksDir)) yield break;
        if (options.DryRun) yield break;

        int batchSentences = UdIngestSupport.ResolveBatchSentences(options);
        long cap = options.MaxInputUnits;
        int workers = IngestParallelism.ResolveFileWorkers(coreHeadroom: 4);
        var files = ListTreebankFiles(treebanksDir, options);
        if (files.Count == 0) yield break;

        ISubstrateReader? reader = context.Reader;
        if (IntentStage.IsBulkFreshBypass) reader = null;

        if (workers <= 1 || files.Count == 1)
        {
            await foreach (var change in IngestFilesSerialAsync(files, reader, batchSentences, cap, ct))
                yield return change;
            yield break;
        }

        var fileQueue = new ConcurrentQueue<string>(files);
        var channel = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(workers * 4)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        var producers = new Task[workers];
        for (int wi = 0; wi < workers; wi++)
        {
            int worker = wi;
            producers[wi] = Task.Run(async () =>
            {
                while (fileQueue.TryDequeue(out var conllu))
                {
                    ct.ThrowIfCancellationRequested();
                    string stem = Path.GetFileNameWithoutExtension(conllu);
                    await foreach (var change in IngestOneFileAsync(
                        conllu, reader, batchSentences, $"ud/w{worker}/{stem}", maxInputUnits: 0, ct))
                        await channel.Writer.WriteAsync(change, ct);
                    await channel.Writer.WriteAsync(PeriodBoundary(stem), ct);
                }
            }, ct);
        }

        _ = Task.WhenAll(producers).ContinueWith(
            t => channel.Writer.TryComplete(t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (var change in channel.Reader.ReadAllAsync(ct))
            yield return change;
        await Task.WhenAll(producers);
    }

    private async IAsyncEnumerable<SubstrateChange> IngestFilesSerialAsync(
        List<string> files,
        ISubstrateReader? reader,
        int batchSentences,
        long maxInputUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long consumed = 0;
        foreach (string conllu in files)
        {
            ct.ThrowIfCancellationRequested();
            if (maxInputUnits > 0 && consumed >= maxInputUnits) yield break;
            long fileCap = maxInputUnits > 0 ? maxInputUnits - consumed : 0;
            string stem = Path.GetFileNameWithoutExtension(conllu);
            await foreach (var change in IngestOneFileAsync(
                conllu, reader, batchSentences, $"ud/{stem}", fileCap, ct))
            {
                consumed += change.Metadata.InputUnitsConsumed;
                yield return change;
                if (maxInputUnits > 0 && consumed >= maxInputUnits) yield break;
            }
            yield return PeriodBoundary(stem);
            if (maxInputUnits > 0 && consumed >= maxInputUnits) yield break;
        }
    }

    private async IAsyncEnumerable<SubstrateChange> IngestOneFileAsync(
        string conllu,
        ISubstrateReader? reader,
        int batchSentences,
        string batchLabelPrefix,
        long maxInputUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string langCode = UdIngestSupport.ExtractLangCode(Path.GetFileName(conllu));
        Hash128 langId = LanguageReference.Resolve(langCode);
        var stream = new UdConlluFileStream(conllu, langId, langCode);
        var handler = new UdIngestHandler(Source, _canonicalNames);
        var config = UdIngestSupport.PipelineConfig(
            Source, batchLabelPrefix, batchSentences, reader, maxInputUnits);
        await foreach (var change in IngestBatchPipeline.RunAsync(stream, handler, config, ct))
        {
            handler.ResetBatchState();
            yield return change;
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string treebanksDir = Path.Combine(context.EcosystemPath, "ud-treebanks-v2.17");
        if (!Directory.Exists(treebanksDir))
            return Task.FromResult<IngestInventory?>(null);
        var paths = ListTreebankFiles(treebanksDir, options);
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.FromFiles("sentences", paths, options.MaxInputUnits, ct));
        var files = paths.Select(p =>
        {
            string id = Path.GetFileNameWithoutExtension(p);
            return new IngestFileSpec(id, p, EtlInventory.CountConlluSentences(p));
        }).ToList();
        long total = files.Sum(f => f.InputUnits);
        return Task.FromResult<IngestInventory?>(new IngestInventory("sentences", total, files));
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static SubstrateChange PeriodBoundary(string stem) =>
        new SubstrateChangeBuilder(Source, $"period-boundary/{stem}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0).Build();

    private static LanguageFilter? EffectiveLanguages(DecomposerOptions options) =>
        options.Languages is { IsActive: true } ? options.Languages
        : LanguageFilter.ForSource("UDDecomposer");

    private static List<string> ListTreebankFiles(string treebanksDir, DecomposerOptions options)
    {
        var all = Directory.EnumerateFiles(treebanksDir, "*.conllu", SearchOption.AllDirectories).ToList();
        var langs = EffectiveLanguages(options);
        if (langs is { IsActive: true })
            return all.Where(p => langs.MatchesUdTreebankFile(Path.GetFileName(p))).ToList();
        Console.Error.WriteLine($"UD: no language filter — ingesting all {all.Count} treebank files (multilingual).");
        return all;
    }
}
