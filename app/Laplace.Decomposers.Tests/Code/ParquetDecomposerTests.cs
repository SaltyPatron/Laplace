using System.Globalization;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Code;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace Laplace.Decomposers.Code.Tests;

/// <summary>
/// Pure extraction-side coverage for the generic <see cref="ParquetDecomposer"/> —
/// the container-strip reader (<see cref="SharedParquetRecordStream.ReadGenericRowsAsync"/>)
/// and the cell-normalization the decomposer applies before witnessing. No DB / native
/// perfcache dependency: these exercise the record-production boundary only.
/// </summary>
public sealed class ParquetDecomposerTests
{
    private static readonly DataField<int> IdField = new("id");
    private static readonly DataField<string> NameField = new("name");
    private static readonly DataField<double> ScoreField = new("score");
    private static readonly DataField<bool> ActiveField = new("active");
    private static readonly DataField<string> NoteField = new("note");

    private static async Task WriteFixtureAsync(string path)
    {
        var schema = new ParquetSchema(IdField, NameField, ScoreField, ActiveField, NoteField);
        await using var fs = File.Create(path);
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, fs);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();
        await rg.WriteAsync(IdField, new ReadOnlyMemory<int>([1, 2, 3]));
        await rg.WriteAsync(NameField, new[] { "alice", "bob", "carol" });
        await rg.WriteAsync(ScoreField, new ReadOnlyMemory<double>([1.5, 2.0, 3.25]));
        await rg.WriteAsync(ActiveField, new ReadOnlyMemory<bool>([true, false, true]));
        await rg.WriteAsync(NoteField, new string?[] { "hi", null, "  " });
    }

    [Fact]
    public async Task ReadGenericRows_StreamsEveryRow_WithNativeCellValues()
    {
        var dir = Directory.CreateTempSubdirectory("parquet-read-test");
        try
        {
            string file = Path.Combine(dir.FullName, "data.parquet");
            await WriteFixtureAsync(file);

            var rows = new List<IReadOnlyList<SharedParquetRecordStream.GenericCell>>();
            await foreach (var row in SharedParquetRecordStream.ReadGenericRowsAsync(file, default))
                rows.Add(row);

            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(5, r.Count));
            Assert.Equal(
                ["id", "name", "score", "active", "note"],
                rows[0].Select(c => c.Column).ToArray());

            Assert.Equal(1, rows[0][0].Value);
            Assert.Equal("alice", rows[0][1].Value);
            Assert.Equal(1.5, rows[0][2].Value);
            Assert.Equal(true, rows[0][3].Value);
            Assert.Equal("hi", rows[0][4].Value);

            Assert.Equal(2, rows[1][0].Value);
            Assert.Null(rows[1][4].Value); // SQL NULL preserved as null
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EnumerateParquet_ResolvesSingleFileAndDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("parquet-enum-test");
        try
        {
            string file = Path.Combine(dir.FullName, "data.parquet");
            await WriteFixtureAsync(file);

            Assert.Equal(
                [file],
                SharedParquetRecordStream.EnumerateParquet(file, SearchOption.AllDirectories).ToArray());
            Assert.Equal(
                [file],
                SharedParquetRecordStream.EnumerateParquet(dir.FullName, SearchOption.AllDirectories).ToArray());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FormatCell_NormalizesEachLogicalTypeCultureInvariantly()
    {
        Assert.Null(ParquetDecomposer.FormatCell(null));
        Assert.Null(ParquetDecomposer.FormatCell(""));
        Assert.Null(ParquetDecomposer.FormatCell("   "));
        Assert.Equal("hi", ParquetDecomposer.FormatCell("  hi  "));
        Assert.Equal("true", ParquetDecomposer.FormatCell(true));
        Assert.Equal("false", ParquetDecomposer.FormatCell(false));
        Assert.Equal("42", ParquetDecomposer.FormatCell(42));
        Assert.Equal("42", ParquetDecomposer.FormatCell(42L));
        Assert.Equal("3.25", ParquetDecomposer.FormatCell(3.25));

        // Opaque binary blobs are not witnessable value tokens.
        Assert.Null(ParquetDecomposer.FormatCell(new byte[] { 1, 2, 3 }));

        var dt = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal(dt.ToString("O", CultureInfo.InvariantCulture), ParquetDecomposer.FormatCell(dt));
    }
}
