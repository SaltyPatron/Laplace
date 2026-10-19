using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class TabularDecomposer
    : ComposeDecomposerMultiFile<TabularDecomposer.RowRecord, TabularSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = TabularSource.SourceId;
    public static readonly Hash128 TrustClass = TabularSource.TrustClass;

    private static readonly Hash128 ColumnTypeId = EntityTypeRegistry.TabularColumn;
    private static readonly Hash128 ValueTypeId = EntityTypeRegistry.TabularValue;

    private static readonly HashSet<string> IdLike =
        new(StringComparer.OrdinalIgnoreCase) { "id", "customerid", "rownumber" };

    public TabularDecomposer(string targetColumn = "Exited", string positiveValue = "1", int numBins = 10)
    {
        // A target column and a positive label are an analysis of the table. The file's
        // cells are recorded either way; these arguments are not predicates.
        _ = targetColumn;
        _ = positiveValue;
        _ = numBins;
    }

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "tabular";

    // Dynamic table values are content, not canonical-name registry payloads.
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => Array.Empty<string>();

    protected override async Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        var seed = new SubstrateChangeBuilder(Source, "bootstrap/tabular-vocab", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0)
            .DeclareSourcePrior(SourceTrust);
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options) =>
        EnumerateCsv(ecosystemPath)
            .Select((file, i) => (file, $"tabular/{i}/{Path.GetFileName(file)}"))
            .ToList();

    protected override async IAsyncEnumerable<RowRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string[]? header = await ReadHeaderAsync([filePath], ct);
        if (header is null || header.Length == 0) yield break;

        var columns = header.Where(c => !IdLike.Contains(c)).ToList();
        if (columns.Count == 0) yield break;

        await foreach (var row in StreamRowsAsync([filePath], header, columns, ct))
            yield return row;
    }

    protected override void Compose(RowRecord rec, SubstrateChangeBuilder b)
    {
        foreach (var (col, raw) in rec.Cells)
        {
            string tok = raw.Trim();
            if (tok.Length == 0) continue;

            // The cell is the ordered composition [column, value]. The column name and the
            // cell text are its constituents; a second edge would only restate them.
            OrderedCompositionComponent column = EnsureColumn(b, col);
            OrderedCompositionComponent value = RequireComponent(b, tok);
            Span<OrderedCompositionResult> composed = stackalloc OrderedCompositionResult[1];
            OrderedComposition.StageBatch(b.ContentStage,
                [new OrderedCompositionRequest([column, value], ValueTypeId, Source, 0)], composed);
        }
    }

    private static OrderedCompositionComponent EnsureColumn(SubstrateChangeBuilder b, string col)
    {
        OrderedCompositionComponent column = RequireComponent(b, col);
        // The column entity is the column-name content, typed as a column; no separate
        // flat-string id is minted.
        b.AddEntity(new EntityRow(column.Id, column.Tier, ColumnTypeId));
        return column;
    }

    private static OrderedCompositionComponent RequireComponent(SubstrateChangeBuilder b, string value) =>
        ContentEmitter.StageComponent(b, value, Source)
        ?? throw new InvalidOperationException($"tabular content '{value}' has no decomposed root");

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateCsv(context.EcosystemPath).ToList();
        return Task.FromResult(IngestInventory.FromFiles(
            "rows", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
    }

    public override async Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default) =>
        (await DescribeInputAsync(context, DecomposerOptions.Default, ct))?.TotalInputUnits;

    private static async Task<string[]?> ReadHeaderAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            string[]? header = null;
            await foreach (var (fields, _) in GrammarRowReader.ReadFieldsAsync(
                f, EtlManifest.Get("tabular").Modality, ct))
            {
                header = fields;
                break;
            }
            if (header is { Length: > 0 }) return header;
        }
        return null;
    }

    private async IAsyncEnumerable<RowRecord> StreamRowsAsync(
        IReadOnlyList<string> files,
        string[] header,
        IReadOnlyList<string> columns,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            bool skippedHeader = false;
            await foreach (var (fields, _) in GrammarRowReader.ReadFieldsAsync(
                f, EtlManifest.Get("tabular").Modality, ct))
            {
                if (!skippedHeader) { skippedHeader = true; continue; }
                if (fields.Length != header.Length) continue;

                var rec = new Dictionary<string, string>(columns.Count, StringComparer.Ordinal);
                for (int i = 0; i < header.Length; i++)
                {
                    string col = header[i];
                    if (IdLike.Contains(col)) continue;
                    rec[col] = fields[i];
                }

                yield return new RowRecord(rec);
            }
        }
    }

    private static IEnumerable<string> EnumerateCsv(string root)
    {
        // IngestInput treats the path as one file or a corpus root; a single file must
        // itself be a .csv, which the recursive branch gets from the glob.
        if (IngestInput.IsSingleFile(root))
            return root.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? [root] : [];
        return IngestInput.ResolveFiles(root, "*.csv").OrderBy(p => p, StringComparer.Ordinal);
    }

    public readonly record struct RowRecord(IReadOnlyDictionary<string, string> Cells);
}