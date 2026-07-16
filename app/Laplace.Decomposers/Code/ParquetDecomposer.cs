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
/// pure RECORDING of witnessed structure:
/// <list type="bullet">
///   <item>column entity (<c>TabularColumn</c>) per column, IS_INSTANCE_OF its name content;</item>
///   <item>value entity (<c>TabularValue</c>) per distinct (column, value);</item>
///   <item>value IS_VALUE_IN column; value IS_INSTANCE_OF the bare cell content.</item>
/// </list>
/// Content addressing dedups every column/value across rows, files, and sources — a
/// value that appears in a million rows is stored once and witnessed a million times.
/// </summary>
public sealed class ParquetDecomposer : ComposeDecomposer<ParquetDecomposer.RowRecord, ParquetSource, FullScope>
{
    public static readonly Hash128 Source = ParquetSource.SourceId;
    public static readonly Hash128 TrustClass = ParquetSource.TrustClass;

    private static readonly Hash128 ColumnTypeId = EntityTypeRegistry.TabularColumn;
    private static readonly Hash128 ValueTypeId = EntityTypeRegistry.TabularValue;

    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stagedColumns = new(StringComparer.Ordinal);

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "parquet";
    protected override int DefaultBatchSize => BatchConfigDefaults.HighVolume;

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    private static Hash128 ColumnId(string col) => Hash128.OfCanonical($"parquet/column/{col}/v1");
    private static Hash128 ValueId(string col, string tok) => Hash128.OfCanonical($"parquet/value/{col}={tok}/v1");

    protected override async IAsyncEnumerable<RowRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var files = SharedParquetRecordStream
            .EnumerateParquet(ecosystemPath, SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            if (Directory.Exists(ecosystemPath))
                throw new InvalidOperationException(
                    $"ParquetDecomposer: no *.parquet files under '{ecosystemPath}'");
            yield break;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var row in SharedParquetRecordStream.ReadGenericRowsAsync(file, ct))
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
    }

    protected override void Compose(RowRecord rec, SubstrateChangeBuilder b)
    {
        foreach (var (col, tok) in rec.Cells)
        {
            EnsureColumn(b, col);

            var valueId = ValueId(col, tok);
            b.AddEntity(new EntityRow(valueId, EntityTier.Word, ValueTypeId, Source));
            _canonicalNames.Add($"parquet/value/{col}={tok}/v1");
            b.AddAttestation(NativeAttestation.Categorical(
                valueId, "IS_VALUE_IN", ColumnId(col), Source, TC.StructuredCorpus));
            if (ContentEmitter.Emit(b, tok, Source) is { } bareId)
                b.AddAttestation(NativeAttestation.Categorical(
                    valueId, "IS_INSTANCE_OF", bareId, Source, TC.StructuredCorpus));
        }
    }

    private void EnsureColumn(SubstrateChangeBuilder b, string col)
    {
        if (!_stagedColumns.Add(col)) return;
        b.AddEntity(new EntityRow(ColumnId(col), EntityTier.Word, ColumnTypeId, Source));
        _canonicalNames.Add($"parquet/column/{col}/v1");
        if (ContentEmitter.Emit(b, col, Source) is { } colNameId)
            b.AddAttestation(NativeAttestation.Categorical(
                ColumnId(col), "IS_INSTANCE_OF", colNameId, Source, TC.StructuredCorpus));
    }

    public override Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

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
