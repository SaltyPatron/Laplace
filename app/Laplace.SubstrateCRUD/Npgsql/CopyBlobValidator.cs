using System.Text;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Debug-only forensics for the native PG-binary-COPY blobs produced by an <see cref="IntentStage"/>
/// tuple buffer. Gated entirely by <c>LAPLACE_COPY_VALIDATE=1</c> (<see cref="Enabled"/>): when off,
/// <see cref="Validate"/> / <see cref="Checkpoint"/> are never invoked by the writers, so this has
/// zero runtime cost on the hot path. It has nothing to do with the write algorithm — it only walks
/// an already-serialized blob row-by-row to localize heap corruption to the exact byte/row/phase.
/// </summary>
internal static class CopyBlobValidator
{
    /// <summary>True when <c>LAPLACE_COPY_VALIDATE=1</c> — callers guard their <see cref="Validate"/>
    /// / <see cref="Checkpoint"/> calls with this so the forensics are entirely no-cost when off.</summary>
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("LAPLACE_COPY_VALIDATE") == "1";

    /// <summary>
    /// Diagnostic: re-validate the entities buffer at a named staging phase. Because the entities
    /// buffer provably never reallocs and is only written by complete, checked add_entity rows, any
    /// corruption observed here must come from an EXTERNAL heap write (e.g. a buffer overrun by a
    /// later staging phase). Checkpointing after each phase names the exact phase that introduces
    /// the corruption.
    /// </summary>
    public static void Checkpoint(IntentStage stage, string phase)
    {
        if (!Enabled) return;
        int n = stage.EntityCount;
        if (n == 0) return;
        (IntPtr ptr, long len) = stage.TupleBuffer(IntentStageTable.Entities);
        try
        {
            Validate(ptr, len, 4, "entities", n);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ENTITIES buffer corruption FIRST observed at phase '{phase}' " +
                $"(entityCount={n}, entities.len={len}, expected={(long)n} rows). " +
                $"This localizes the heap corruptor to whatever ran BEFORE this checkpoint. {ex.Message}", ex);
        }
    }

    // Walks the native PG-binary-COPY blob row-by-row and verifies each row begins with
    // the expected int16 field count and that field length prefixes stay within bounds.
    // On the FIRST framing violation it throws with the exact byte offset, the row index,
    // the expected vs observed field count, and a hex window so we can see what bytes
    // corrupted the stream (recognizable ASCII / hash / double patterns reveal the source).
    public static void Validate(IntPtr ptr, long len, int expectedFields, string tableName, int rowCount)
    {
        if (ptr == IntPtr.Zero || len <= 0) return;
        var blob = GC.AllocateUninitializedArray<byte>(checked((int)len));
        unsafe
        {
            new ReadOnlySpan<byte>((void*)ptr, checked((int)len)).CopyTo(blob);
        }

        // Entities are STRICTLY fixed-size: int16(4) + [int32(16)+16 id] + [int32(2)+2 tier]
        // + [int32(16)+16 type_id] + [int32(16)+16 fob | int32(-1) NULL fob].
        // => stride is 68 bytes (fob present) or 52 bytes (fob NULL). Because the stream
        // is fixed-size, the FIRST row whose start is not reachable by a valid stride from
        // the previous row start is the true point of corruption. We detect that precisely
        // and, on the row-by-row walk below, also confirm field counts/lengths.
        long off = 0;
        int row = 0;
        while (off < len)
        {
            long rowStart = off;
            if (off + 2 > len)
                FailCopyBlob(blob, rowStart, row, tableName, expectedFields, -1,
                    "truncated field-count (need 2 bytes)");
            int fields = (blob[off] << 8) | blob[off + 1];
            off += 2;
            if (fields != expectedFields)
                FailCopyBlob(blob, rowStart, row, tableName, expectedFields, fields,
                    $"unexpected field count (got {fields})");
            for (int f = 0; f < fields; f++)
            {
                if (off + 4 > len)
                    FailCopyBlob(blob, rowStart, row, tableName, expectedFields, fields,
                        $"truncated length prefix at field {f}");
                int flen = (blob[off] << 24) | (blob[off + 1] << 16) | (blob[off + 2] << 8) | blob[off + 3];
                off += 4;
                if (flen == -1) continue;          // NULL field
                if (flen < 0 || off + flen > len)
                    FailCopyBlob(blob, rowStart, row, tableName, expectedFields, fields,
                        $"field {f} length {flen} overruns blob (off={off}, len={len})");
                off += flen;
            }
            row++;
        }
        if (row != rowCount)
            throw new InvalidOperationException(
                $"COPY blob validation: {tableName} parsed {row} rows but stage reports {rowCount}.");
    }

    private static void FailCopyBlob(
        byte[] blob, long rowStart, int row, string tableName, int expected, int got, string why)
    {
        long winStart = Math.Max(0, rowStart - 160);
        long winEnd = Math.Min(blob.LongLength, rowStart + 160);
        var sb = new StringBuilder();
        for (long i = winStart; i < winEnd; i++)
        {
            if (i == rowStart) sb.Append("[>");
            sb.Append(blob[i].ToString("X2"));
            if (i == rowStart) sb.Append("<]");
            sb.Append(' ');
        }
        var ascii = new StringBuilder();
        for (long i = winStart; i < winEnd; i++)
        {
            byte c = blob[i];
            ascii.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
        }

        // Walk forward from the blob start to find the FIRST row that fails to land on a
        // valid fixed-stride boundary (entities only). This pinpoints the originating
        // corrupt row rather than the row where the desync surfaced.
        var strideReport = new StringBuilder();
        if (tableName == "entities")
        {
            strideReport.Append('\n');
            strideReport.Append(DescribeEntityStride(blob, rowStart));
        }

        throw new InvalidOperationException(
            $"COPY blob CORRUPT in '{tableName}': {why}; row #{row}, rowStart byte offset {rowStart}, " +
            $"expected {expected} fields. Hex window (rowStart marked [>..<]):\n{sb}\nASCII: {ascii}{strideReport}");
    }

    // Re-walks the entities blob assuming the strict fixed layout and reports the first row
    // whose framing deviates, plus a per-row stride trace around the failure offset.
    private static string DescribeEntityStride(byte[] blob, long failOffset)
    {
        var sb = new StringBuilder();
        long off = 0;
        int row = 0;
        long len = blob.LongLength;
        long lastGoodStart = 0;
        while (off + 2 <= len)
        {
            long rowStart = off;
            int fields = (blob[off] << 8) | blob[off + 1];
            if (fields != 4)
            {
                sb.Append($"first off-layout entity row #{row} at offset {rowStart} (field-count={fields}, expected 4); ");
                sb.Append($"previous good row started at {lastGoodStart} (stride {rowStart - lastGoodStart}). ");
                long ws = Math.Max(0, lastGoodStart - 8);
                long we = Math.Min(len, rowStart + 16);
                sb.Append("bytes around prev→bad: ");
                for (long i = ws; i < we; i++)
                {
                    if (i == lastGoodStart) sb.Append("{prev>");
                    if (i == rowStart) sb.Append("{bad>");
                    sb.Append(blob[i].ToString("X2"));
                    sb.Append(' ');
                }
                return sb.ToString();
            }
            // STRICT fixed-layout check: every field length must be exactly as specified.
            // We do NOT follow a wrong length (which would mask the true origin); instead we
            // verify each prefix and stop at the FIRST deviation. Layout:
            //   [int16=4][int32=16 + 16 id][int32=2 + 2 tier][int32=16 + 16 type_id]
            //   [int32=16 + 16 fob | int32=-1 NULL]
            int lId   = ReadLen(blob, rowStart + 2);
            int lTier = ReadLen(blob, rowStart + 2 + 4 + 16);
            int lType = ReadLen(blob, rowStart + 2 + 4 + 16 + 4 + 2);
            int lFob  = ReadLen(blob, rowStart + 2 + 4 + 16 + 4 + 2 + 4 + 16);
            bool ok = lId == 16 && lTier == 2 && lType == 16 && (lFob == 16 || lFob == -1);
            if (!ok)
            {
                sb.Append($"first BAD-LENGTH entity row #{row} at offset {rowStart}: ");
                sb.Append($"id_len={lId}(want 16) tier_len={lTier}(want 2) type_len={lType}(want 16) fob_len={lFob}(want 16 or -1). ");
                sb.Append($"prev good row at {lastGoodStart} (stride {rowStart - lastGoodStart}). ");
                long ws = Math.Max(0, rowStart - 8);
                long we = Math.Min(len, rowStart + 80);
                sb.Append("bytes: ");
                for (long i = ws; i < we; i++)
                {
                    if (i == rowStart) sb.Append("{row>");
                    sb.Append(blob[i].ToString("X2"));
                    sb.Append(' ');
                }
                return sb.ToString();
            }
            long stride = lFob == -1 ? 52 : 68;
            lastGoodStart = rowStart;
            off = rowStart + stride;
            row++;
        }
        sb.Append($"entities STRICT re-walk completed {row} rows up to offset {off} with NO layout break (failOffset={failOffset}); ");
        sb.Append($"this means the byte count is consistent but a field-count read 0 at failOffset — investigate buffer len vs row_count. ");
        return sb.ToString();
    }

    private static int ReadLen(byte[] blob, long at)
    {
        if (at < 0 || at + 4 > blob.LongLength) return -100;
        return (blob[at] << 24) | (blob[at + 1] << 16) | (blob[at + 2] << 8) | (blob[at + 3]);
    }
}
