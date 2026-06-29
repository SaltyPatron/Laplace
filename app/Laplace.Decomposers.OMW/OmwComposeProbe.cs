using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.OMW;

/// <summary>
/// Serial scan: probe + materialize_phys per OMW tab row. Finds the first row that fails
/// native compose without DB or parallel ingest noise.
/// </summary>
public static class OmwComposeProbe
{
    public sealed record Failure(
        long RowIndex,
        string FilePath,
        string Error,
        string LinePreview,
        int LineBytes);

    public static async Task<Failure?> ScanFirstFailureAsync(
        string wnsDir,
        LanguageFilter? langs = null,
        long startRow = 0,
        long maxRows = 0,
        CancellationToken ct = default)
    {
        CodepointPerfcache.LoadDefault();
        var omw = EtlManifest.Get("omw");
        long rowIndex = 0;

        foreach (string tabFile in OMWTabFiles.EnumerateTabFiles(wnsDir, langs).OrderBy(p => p))
        {
            ct.ThrowIfCancellationRequested();
            var stream = GrammarFileRecordStream.ForSource(
                tabFile, omw, line => line.Length > 0 && line[0] != (byte)'#');

            await foreach (var record in stream.RecordsAsync(ct))
            {
                if (rowIndex < startRow)
                {
                    rowIndex++;
                    continue;
                }

                if (maxRows > 0 && rowIndex - startRow >= maxRows)
                    return null;

                GrammarAst ast = record.Ast;
                try
                {
                    using var composer = new GrammarRowComposer(
                        record.LineUtf8, ast, omw.SourceId, omw.Modality.GrammarId);
                    _ = composer.Materialize(1.0);
                }
                catch (Exception ex)
                {
                    string preview = System.Text.Encoding.UTF8.GetString(
                        record.LineUtf8.AsSpan(0, Math.Min(record.LineUtf8.Length, 240)));
                    return new Failure(rowIndex, tabFile, ex.Message, preview, record.LineUtf8.Length);
                }
                finally
                {
                    ast.Dispose();
                }

                rowIndex++;
                if (rowIndex % 100_000 == 0)
                    Console.Error.WriteLine($"omw-probe: row {rowIndex:N0} ok (last file {Path.GetFileName(tabFile)})");
            }
        }

        return null;
    }
}
