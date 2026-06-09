using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>Read delimited rows via native grammar field spans (no line.Split).</summary>
public static class GrammarRowReader
{
    public static async IAsyncEnumerable<(string[] Fields, long UnitsConsumed)> ReadFieldsAsync(
        string filePath,
        string modalityId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) yield break;

        IntPtr iter = CreateRowIter(recipe);
        if (iter == IntPtr.Zero) yield break;

        try
        {
            long units = 0;
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 1 << 20, useAsync: true);
            var buf = new byte[1 << 20];
            int read;
            while ((read = await fs.ReadAsync(buf, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                units += read;
                foreach (var fields in FeedChunkFields(iter, buf, read, modalityId))
                    yield return (fields, units);
            }
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

    private static unsafe List<string[]> FeedChunkFields(IntPtr iter, byte[] buf, int read, string modalityId)
    {
        var rows = new List<string[]>();
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
                var lineUtf8 = new ReadOnlySpan<byte>(row.RowUtf8.ToPointer(), rowLen).ToArray();
                using var ast = GrammarAst.Adopt(row.Ast);
                nativeRows[ri].Ast = IntPtr.Zero;
                using var composer = new GrammarRowComposer(lineUtf8, ast, Hash128.Zero, modalityId);
                var spans = composer.FieldSpans();
                var fields = new string[spans.Count];
                for (int i = 0; i < spans.Count; i++)
                {
                    var sp = spans[i];
                    fields[i] = System.Text.Encoding.UTF8.GetString(
                        lineUtf8.AsSpan((int)sp.Start, (int)(sp.End - sp.Start)));
                }
                rows.Add(fields);
            }
            if (nativeRows != null)
                NativeInterop.GrammarRowIterFreeRows(nativeRows, rowCount);
        }
        return rows;
    }
}
