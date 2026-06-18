using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;













public static class StructuredGrammarIngest
{
    public static async IAsyncEnumerable<SubstrateChange> IngestFileAsync(
        string filePath,
        string modalityId,
        Hash128 sourceId,
        IGrammarWitness witness,
        int batchSize,
        double witnessWeight,
        string batchLabelPrefix,
        Action<long>? reportUnits,
        Hash128? contextId = null,
        int commitEpoch = 0,
        Func<ReadOnlySpan<byte>, bool>? acceptRow = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) yield break;

        IntPtr iter = CreateRowIter(recipe);
        if (iter == IntPtr.Zero) yield break;

        try
        {
            var b = NewBuilder(sourceId, batchLabelPrefix, 0, batchSize, commitEpoch);
            int inBatch = 0, bn = 0, rowIndex = 0;
            long rowsTotal = 0;
            long rowsInBatch = 0;

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 1 << 20, useAsync: true);
            var buf = new byte[1 << 20];
            int read;
            while ((read = await fs.ReadAsync(buf, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var row in FeedChunk(iter, buf, read))
                {
                    rowsTotal++;
                    if (acceptRow is not null && !acceptRow(row.LineUtf8))
                        continue;

                    rowsInBatch++;
                    using var ast = GrammarAst.Adopt(row.Ast);
                    using var composer = new GrammarRowComposer(row.LineUtf8, ast, sourceId, modalityId);
                    var (ents, phys, atts, root) = composer.Materialize(witnessWeight);

                    foreach (var e in ents) b.AddEntity(e);
                    foreach (var p2 in phys) b.AddPhysicality(p2);
                    foreach (var a in atts) b.AddAttestation(a);

                    witness.WalkRow(
                        new GrammarComposeContext(row.LineUtf8, ast, root, composer),
                        new RowContext(rowIndex++, rowsTotal, contextId),
                        b);

                    if (reportUnits is not null && rowsTotal % 100 == 0)
                        reportUnits(rowsTotal);

                    if (++inBatch >= batchSize)
                    {
                        yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
                        bn++;
                        b = NewBuilder(sourceId, batchLabelPrefix, bn, batchSize, commitEpoch);
                        inBatch = 0;
                        rowsInBatch = 0;
                    }
                }

                reportUnits?.Invoke(rowsTotal);
            }

            if (inBatch > 0)
                yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
        }
        finally
        {
            if (iter != IntPtr.Zero)
                NativeInterop.GrammarRowIterFree(iter);
        }
    }

    private static unsafe IntPtr CreateRowIter(IntPtr recipe)
    {
        IntPtr iter = IntPtr.Zero;
        return NativeInterop.GrammarRowIterNew(recipe, &iter) == 0 ? iter : IntPtr.Zero;
    }

    private static unsafe List<ParsedRow> FeedChunk(IntPtr iter, byte[] buf, int read)
    {
        var rows = new List<ParsedRow>();
        NativeInterop.ParsedRowNative* nativeRows = null;
        nuint rowCount = 0;
        fixed (byte* p = buf)
        {
            if (NativeInterop.GrammarRowIterFeed(iter, p, (nuint)read, &nativeRows, &rowCount) != 0)
                return rows;

            for (nuint ri = 0; ri < rowCount; ri++)
            {
                var row = nativeRows[ri];
                int rowLen = (int)row.RowLen.ToUInt64();
                rows.Add(new ParsedRow(
                    row.Ast,
                    new ReadOnlySpan<byte>(row.RowUtf8.ToPointer(), rowLen).ToArray()));
                nativeRows[ri].Ast = IntPtr.Zero;
            }
            if (nativeRows != null)
                NativeInterop.GrammarRowIterFreeRows(nativeRows, rowCount);
        }
        return rows;
    }

    private readonly record struct ParsedRow(IntPtr Ast, byte[] LineUtf8);

    private static SubstrateChangeBuilder NewBuilder(
        Hash128 sourceId, string prefix, int bn, int batchSize, int commitEpoch) =>
        new SubstrateChangeBuilder(sourceId, $"{prefix}/{bn}", null,
            entityCapacity: batchSize * 32,
            physicalityCapacity: batchSize * 32,
            attestationCapacity: batchSize * 8)
            .SetCommitEpoch(commitEpoch);

    public static async Task<SubstrateChange?> IngestJsonDocumentAsync(
        string filePath,
        string modalityId,
        Hash128 sourceId,
        IGrammarWitness witness,
        double witnessWeight,
        string batchLabel,
        CancellationToken ct = default)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) return null;

        byte[] utf8 = await File.ReadAllBytesAsync(filePath, ct);
        if (utf8.Length == 0) return null;

        using var ast = GrammarDecomposer.Parse(utf8, recipe);
        using var composer = new GrammarRowComposer(utf8, ast, sourceId, modalityId);
        var (ents, phys, atts, root) = composer.Materialize(witnessWeight);

        var b = NewBuilder(sourceId, batchLabel, 0, 1, commitEpoch: 0);
        foreach (var e in ents) b.AddEntity(e);
        foreach (var p in phys) b.AddPhysicality(p);
        foreach (var a in atts) b.AddAttestation(a);

        var ctx = new GrammarComposeContext(utf8, ast, root, composer);
        witness.WalkRow(ctx, new RowContext(0, 1), b);
        return b.SetInputUnitsConsumed(1).Build();
    }
}
