using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OpenSubtitles;

public sealed class OpenSubtitlesDecomposer : IDecomposer, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OpenSubtitlesDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly (string Pair, long Pairs)[] PairCounts =
    {
        ("ar-en",     87_893_588L),
        ("de-en",     65_673_701L),
        ("en-es",    105_482_431L),
        ("en-fr",     83_896_581L),
        ("en-it",     72_430_053L),
        ("en-ja",      2_068_294L),
        ("en-ko",     31_052_957L),
        ("en-pt",     68_557_861L),
        ("en-ru",     61_544_952L),
        ("en-zh_CN",  22_394_812L),
    };

    public Hash128 SourceId     => Source;
    public string  SourceName   => "OpenSubtitlesDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("IS_TRANSLATION_OF");
        boot.AddRelationType("HAS_LANGUAGE");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(context.EcosystemPath)) yield break;

        int batch = options.BatchSize > 1 ? options.BatchSize : 8192;
        int workers = ResolveDecomposeWorkers();
        int chunk = Math.Clamp(batch / 4, 512, 4096);
        var zips = SelectZips(context.EcosystemPath, options);
        if (zips.Count == 0) yield break;

        if (workers <= 1)
        {
            foreach (var (zipPath, pairStem) in zips)
            {
                ct.ThrowIfCancellationRequested();
                await foreach (var change in IngestZipSerialAsync(zipPath, pairStem, batch, ct))
                {
                    if (!options.DryRun) yield return change;
                }
            }
            yield break;
        }

        var pairChannel = Channel.CreateBounded<OpenSubtitlesLinePair>(
            new BoundedChannelOptions(Math.Max(workers * 512, 8192))
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        var microChannel = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(workers * 8)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        var zipQueue = new ConcurrentQueue<(string Path, string Stem)>(zips);
        int readerCount = Math.Min(workers, zips.Count);
        var readers = new Task[readerCount];
        for (int ri = 0; ri < readerCount; ri++)
        {
            readers[ri] = Task.Run(async () =>
            {
                while (zipQueue.TryDequeue(out var zip))
                {
                    ct.ThrowIfCancellationRequested();
                    await foreach (var pair in OpenSubtitlesFastIngest.ReadZipPairsAsync(zip.Path, zip.Stem, ct))
                        await pairChannel.Writer.WriteAsync(pair, ct);
                }
            }, ct);
        }

        _ = Task.WhenAll(readers).ContinueWith(
            t => pairChannel.Writer.TryComplete(t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var processors = new Task[workers];
        for (int wi = 0; wi < workers; wi++)
        {
            int worker = wi;
            processors[wi] = Task.Run(async () =>
            {
                SubstrateChangeBuilder? local = null;
                Hash128 langA = default, langB = default;
                string? stem = null;
                int count = 0, bn = 0;

                await foreach (var pair in pairChannel.Reader.ReadAllAsync(ct))
                {
                    if (stem != pair.PairStem)
                    {
                        if (local is not null && count > 0)
                            await microChannel.Writer.WriteAsync(
                                local.SetInputUnitsConsumed(count).Build(), ct);
                        stem = pair.PairStem;
                        langA = pair.LangA;
                        langB = pair.LangB;
                        bn = 0;
                        local = OpenSubtitlesFastIngest.NewBuilder(
                            $"opensubtitles/w{worker}/{stem}/0", chunk, langA, langB);
                        count = 0;
                    }
                    else if (local is null)
                    {
                        stem = pair.PairStem;
                        langA = pair.LangA;
                        langB = pair.LangB;
                        local = OpenSubtitlesFastIngest.NewBuilder(
                            $"opensubtitles/w{worker}/{stem}/0", chunk, langA, langB);
                    }

                    if (!OpenSubtitlesFastIngest.TryAppendPair(local, pair, out _))
                        continue;

                    if (++count >= chunk)
                    {
                        await microChannel.Writer.WriteAsync(
                            local.SetInputUnitsConsumed(count).Build(), ct);
                        bn++;
                        local = OpenSubtitlesFastIngest.NewBuilder(
                            $"opensubtitles/w{worker}/{stem}/{bn}", chunk, langA, langB);
                        count = 0;
                    }
                }

                if (local is not null && count > 0)
                    await microChannel.Writer.WriteAsync(
                        local.SetInputUnitsConsumed(count).Build(), ct);
            }, ct);
        }

        _ = Task.WhenAll(processors).ContinueWith(
            t => microChannel.Writer.TryComplete(t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        SubstrateChangeBuilder? acc = null;
        long accUnits = 0;
        int accBn = 0;

        await foreach (var micro in microChannel.Reader.ReadAllAsync(ct))
        {
            if (options.DryRun) continue;

            if (acc is null)
            {
                acc = new SubstrateChangeBuilder(
                    Source, $"opensubtitles/batch/{accBn}", null,
                    entityCapacity: batch * 4,
                    physicalityCapacity: batch * 8,
                    attestationCapacity: batch * 4);
                accUnits = 0;
            }

            OpenSubtitlesFastIngest.Absorb(acc, micro);
            accUnits += Math.Max(1, micro.Metadata.InputUnitsConsumed);

            if (accUnits >= batch)
            {
                yield return acc.SetInputUnitsConsumed(accUnits).Build();
                accBn++;
                acc = null;
                accUnits = 0;
            }
        }

        if (acc is not null && accUnits > 0)
            yield return acc.SetInputUnitsConsumed(accUnits).Build();

        await Task.WhenAll(readers.Concat(processors));
    }

    private static async IAsyncEnumerable<SubstrateChange> IngestZipSerialAsync(
        string zipPath,
        string pairStem,
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string unitStem = Path.GetFileNameWithoutExtension(zipPath);
        SubstrateChangeBuilder? b = null;
        Hash128 langA = default, langB = default;
        int n = 0, bn = 0;

        await foreach (var pair in OpenSubtitlesFastIngest.ReadZipPairsAsync(zipPath, pairStem, ct))
        {
            if (b is null)
            {
                langA = pair.LangA;
                langB = pair.LangB;
                b = OpenSubtitlesFastIngest.NewBuilder($"opensubtitles/{unitStem}/0", batchSize, langA, langB);
            }

            if (!OpenSubtitlesFastIngest.TryAppendPair(b, pair, out _)) continue;

            if (++n >= batchSize)
            {
                yield return b.SetInputUnitsConsumed(n).Build();
                bn++;
                b = OpenSubtitlesFastIngest.NewBuilder(
                    $"opensubtitles/{unitStem}/{bn}", batchSize, langA, langB);
                n = 0;
            }
        }

        if (b is not null && n > 0)
            yield return b.SetInputUnitsConsumed(n).Build();
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (!Directory.Exists(context.EcosystemPath)) return Task.FromResult<IngestInventory?>(null);
        var files = SelectZips(context.EcosystemPath, options)
            .Select(z =>
            {
                long pairs = PairCounts.FirstOrDefault(x => x.Pair == z.Stem).Pairs;
                return new IngestFileSpec(z.Stem, z.Path, pairs);
            })
            .ToList();
        long total = 0;
        foreach (var f in files) total += f.InputUnits;
        return Task.FromResult<IngestInventory?>(new IngestInventory("pairs", total, files));
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.ForWitness(SourceName), ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static HashSet<string>? ResolvePairAllowlist()
    {
        string? env = Environment.GetEnvironmentVariable("LAPLACE_OPENSUBTITLES_PAIRS");
        if (string.IsNullOrWhiteSpace(env)) return null;
        return env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int ResolveDecomposeWorkers() =>
        int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS"), out var w) && w > 0
            ? w
            : Math.Clamp(Environment.ProcessorCount - 2, 1, 16);

    private static List<(string Path, string Stem)> SelectZips(string dir, DecomposerOptions options)
    {
        var pairAllow = ResolvePairAllowlist();
        var list = new List<(string, string)>();
        foreach (string zipPath in Directory.EnumerateFiles(dir, "*.txt.zip").OrderBy(p => p, StringComparer.Ordinal))
        {
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(zipPath));
            if (pairAllow is not null && !pairAllow.Contains(stem)) continue;
            if (options.Languages?.MatchesLanguagePair(stem) == false) continue;
            list.Add((zipPath, stem));
        }
        return list;
    }
}
