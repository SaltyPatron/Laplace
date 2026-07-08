using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Tab-separated correspondence bridges (MapNet frame→LU, WordFrameNet, OMW .tab rows).
/// Pure extract — yields <see cref="CategoryCorrespondenceRecord"/> rows, no SQL.
/// </summary>
public static class TabBridgeHelpers
{
    public static async IAsyncEnumerable<CategoryCorrespondenceRecord> ReadMapNetFrameRowsAsync(
        string path, long maxInputUnits, [EnumeratorCancellation] CancellationToken ct)
    {
        long consumed = 0;
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (lineMem.Length == 0) continue;
            if (maxInputUnits > 0 && consumed >= maxInputUnits) yield break;
            if (!TryParseTwoColumn(lineMem.Span, out var col0, out var col1)) continue;
            if (col0.IsEmpty || col1.IsEmpty) continue;
            yield return new CategoryCorrespondenceRecord(
                System.Text.Encoding.UTF8.GetString(col0),
                EntityTypeRegistry.FrameNetFrame,
                Hash128.OfCanonical($"framenet/lu/{System.Text.Encoding.UTF8.GetString(col1)}/v1"));
            consumed++;
        }
    }

    public static async IAsyncEnumerable<CategoryCorrespondenceRecord> ReadTwoColumnBridgeAsync(
        string path,
        Func<ReadOnlySpan<byte>, string> subjectKey,
        Hash128 subjectTypeId,
        Func<ReadOnlySpan<byte>, Hash128> objectId,
        string relationType = "CORRESPONDS_TO",
        long maxInputUnits = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long consumed = 0;
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (lineMem.Length == 0) continue;
            if (maxInputUnits > 0 && consumed >= maxInputUnits) yield break;
            if (!TryParseTwoColumn(lineMem.Span, out var col0, out var col1)) continue;
            if (col0.IsEmpty || col1.IsEmpty) continue;
            yield return new CategoryCorrespondenceRecord(
                subjectKey(col0), subjectTypeId, objectId(col1), relationType);
            consumed++;
        }
    }

    private static bool TryParseTwoColumn(
        ReadOnlySpan<byte> line, out ReadOnlySpan<byte> col0, out ReadOnlySpan<byte> col1)
    {
        col0 = col1 = default;
        int tab = line.IndexOf((byte)'\t');
        if (tab < 0) return false;
        col0 = TrimBytes(line[..tab]);
        col1 = TrimBytes(line[(tab + 1)..]);
        return true;
    }

    private static ReadOnlySpan<byte> TrimBytes(ReadOnlySpan<byte> span)
    {
        int start = 0;
        while (start < span.Length && (span[start] == (byte)' ' || span[start] == (byte)'\t')) start++;
        int end = span.Length - 1;
        while (end >= start && (span[end] == (byte)' ' || span[end] == (byte)'\t')) end--;
        return span[start..(end + 1)];
    }
}
