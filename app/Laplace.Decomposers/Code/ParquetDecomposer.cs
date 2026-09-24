using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

/// <summary>
/// Generic Parquet decomposer — witnesses an arbitrary Parquet file/dataset by
/// stripping the container (row groups / columns) and recording each cell exactly as
/// the CSV <see cref="TabularDecomposer"/> records a table. Parquet is packaging; the
/// column schema carries the semantics. No target/outcome interpretation — this is
/// pure RECORDING of witnessed structure.
///
/// Column names and cell values are ordinary decomposed content. A tabular value is
/// the native ordered composition [column-content, value-content], so its identity,
/// geometry and trajectory come from the substrate composition law rather than from a
/// formatted string hash or a canonical-name side table.
/// </summary>
public sealed class ParquetDecomposer
    : ComposeDecomposerMultiFile<ParquetDecomposer.RowRecord, ParquetSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = ParquetSource.SourceId;
    public static readonly Hash128 TrustClass = ParquetSource.TrustClass;

    private static readonly Hash128 ColumnTypeId = EntityTypeRegistry.TabularColumn;
    private static readonly Hash128 ValueTypeId = EntityTypeRegistry.TabularValue;

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "parquet";

    public override IReadOnlyCollection<string> CanonicalNamesForReadback => Array.Empty<string>();

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        var files = SharedParquetRecordStream
            .EnumerateParquet(ecosystemPath, SearchOption.AllDirectories).ToList();
        if (files.Count == 0 && Directory.Exists(ecosystemPath))
            throw new InvalidOperationException(
                $"ParquetDecomposer: no *.parquet files under '{ecosystemPath}'");
        return files.Select((file, i) => (file, $"parquet/{i}/{Path.GetFileName(file)}")).ToList();
    }

    protected override async IAsyncEnumerable<RowRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in SharedParquetRecordStream.ReadGenericRowsAsync(filePath, ct))
        {
            var cells = new List<(string Column, string Value)>(row.Count);
            foreach (var cell in row)
            {
                string? tok = FormatCell(cell.Value);
                if (tok is null) continue;
                cells.Add((cell.Column, tok));
            }
            if (cells.Count == 0) continue;
            yield return new RowRecord(cells);
        }
    }

    protected override void Compose(RowRecord rec, SubstrateChangeBuilder b)
    {
        foreach (var (col, tok) in rec.Cells)
        {
            OrderedCompositionComponent column = EnsureColumn(b, col);
            OrderedCompositionComponent value = RequireComponent(b, tok);
            Span<OrderedCompositionResult> composed = stackalloc OrderedCompositionResult[1];
            OrderedComposition.StageBatch(b.ContentStage,
                [new OrderedCompositionRequest([column, value], ValueTypeId, Source, 0)], composed);
            Hash128 valueId = composed[0].Id;

            b.AddAttestation(NativeAttestation.Categorical(
                valueId, "IS_VALUE_IN", column.Id, Source, TC.StructuredCorpus));
            b.AddAttestation(NativeAttestation.Categorical(
                valueId, "IS_INSTANCE_OF", value.Id, Source, TC.StructuredCorpus));
        }
    }

    private static OrderedCompositionComponent EnsureColumn(SubstrateChangeBuilder b, string col)
    {
        OrderedCompositionComponent column = RequireComponent(b, col);
        b.AddEntity(new EntityRow(column.Id, column.Tier, ColumnTypeId));
        return column;
    }

    private static OrderedCompositionComponent RequireComponent(SubstrateChangeBuilder b, string value) =>
        ContentEmitter.StageComponent(b, value, Source)
        ?? throw new InvalidOperationException($"parquet content '{value}' has no decomposed root");

    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var files = SharedParquetRecordStream
            .EnumerateParquet(context.EcosystemPath, SearchOption.AllDirectories).ToList();
        if (files.Count == 0) return null;
        var specs = new List<IngestFileSpec>(files.Count);
        long total = 0;
        foreach (var file in files)
        {
            long rows = await SharedParquetRecordStream.CountRowsAsync(file, ct);
            specs.Add(new IngestFileSpec(Path.GetFileName(file), file, rows));
            total += rows;
        }
        if (options.MaxInputUnits > 0) total = Math.Min(total, options.MaxInputUnits);
        return new IngestInventory("rows", total, specs, TracksFileCompletion: true);
    }

    public override async Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default) =>
        (await DescribeInputAsync(context, DecomposerOptions.Default, ct))?.TotalInputUnits;

    /// <summary>
    /// Normalize a native Parquet cell value to its canonical token, or null when the
    /// cell carries no witnessable value (SQL NULL, empty string, or an opaque binary
    /// blob). Numeric/temporal types are rendered culture-invariantly so the same
    /// logical value content-addresses identically across files and locales.
    /// </summary>
    internal static string? FormatCell(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                s = s.Trim();
                return s.Length == 0 ? null : s;
            case bool bo:
                return bo ? "true" : "false";
            case byte[]:
                // Opaque binary column — not a value token; witnessing raw blobs as
                // string tokens would balloon content with non-semantic bytes.
                return null;
            case DateTime dt:
                return dt.ToString("O", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString("O", CultureInfo.InvariantCulture);
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            default:
                string? t = value.ToString();
                if (t is null) return null;
                t = t.Trim();
                return t.Length == 0 ? null : t;
        }
    }

    public readonly record struct RowRecord(IReadOnlyList<(string Column, string Value)> Cells);
}