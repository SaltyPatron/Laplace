using System.Text;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

internal static unsafe class CopyBlobValidator
{
    // Default ON. Walking the native COPY blobs each CollectBlobs pass turns silent heap
    // corruption into a loud error AT the corrupting phase instead of a fail-fast 6MB
    // downstream in CopyTupleParser. The cost is negligible against a multi-hour seed, and
    // a correctness check that catches memory corruption must never be opt-in — the safe
    // path is the default. Explicit opt-out (LAPLACE_COPY_BLOB_VALIDATE=0) exists only for
    // clean-run micro-benchmarking, never for production seeds.
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("LAPLACE_COPY_BLOB_VALIDATE") != "0";

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

    // Walk the native COPY-tuple buffer IN PLACE with long offsets. The buffer is the
    // IntentStage's UNMANAGED tuple arena, so there is no GC pin required and no 2 GiB
    // ceiling: a large working-set apply (a monolithic UD/ConceptNet/chess flush is tens of
    // millions of rows / multiple GiB in one buffer) is validated directly. The previous
    // implementation copied the whole buffer into a managed byte[] via
    // GC.AllocateUninitializedArray(checked((int)len)) — which (a) threw OverflowException
    // the instant a single stage buffer crossed int.MaxValue, aborting the entire lane with
    // committed=0, and (b) doubled resident memory by cloning every multi-GiB buffer on
    // every apply. Neither is acceptable for a correctness check.
    public static void Validate(IntPtr ptr, long len, int expectedFields, string tableName, int rowCount)
    {
        if (ptr == IntPtr.Zero || len <= 0) return;
        byte* blob = (byte*)ptr;

        long off = 0;
        int row = 0;
        while (off < len)
        {
            long rowStart = off;
            if (off + 2 > len)
                FailCopyBlob(blob, len, rowStart, row, tableName, expectedFields, -1,
                    "truncated field-count (need 2 bytes)");
            int fields = (blob[off] << 8) | blob[off + 1];
            off += 2;
            if (fields != expectedFields)
                FailCopyBlob(blob, len, rowStart, row, tableName, expectedFields, fields,
                    $"unexpected field count (got {fields})");
            for (int f = 0; f < fields; f++)
            {
                if (off + 4 > len)
                    FailCopyBlob(blob, len, rowStart, row, tableName, expectedFields, fields,
                        $"truncated length prefix at field {f}");
                int flen = (blob[off] << 24) | (blob[off + 1] << 16) | (blob[off + 2] << 8) | blob[off + 3];
                off += 4;
                if (flen == -1) continue;
                if (flen < 0 || off + flen > len)
                    FailCopyBlob(blob, len, rowStart, row, tableName, expectedFields, fields,
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
        byte* blob, long len, long rowStart, int row, string tableName, int expected, int got, string why)
    {
        long winStart = Math.Max(0, rowStart - 160);
        long winEnd = Math.Min(len, rowStart + 160);
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




        var strideReport = new StringBuilder();
        if (tableName == "entities")
        {
            strideReport.Append('\n');
            strideReport.Append(DescribeEntityStride(blob, len, rowStart));
        }

        throw new InvalidOperationException(
            $"COPY blob CORRUPT in '{tableName}': {why}; row #{row}, rowStart byte offset {rowStart}, " +
            $"expected {expected} fields. Hex window (rowStart marked [>..<]):\n{sb}\nASCII: {ascii}{strideReport}");
    }



    private static string DescribeEntityStride(byte* blob, long len, long failOffset)
    {
        var sb = new StringBuilder();
        long off = 0;
        int row = 0;
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





            int lId = ReadLen(blob, len, rowStart + 2);
            int lTier = ReadLen(blob, len, rowStart + 2 + 4 + 16);
            int lType = ReadLen(blob, len, rowStart + 2 + 4 + 16 + 4 + 2);
            int lFob = ReadLen(blob, len, rowStart + 2 + 4 + 16 + 4 + 2 + 4 + 16);
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

    private static int ReadLen(byte* blob, long len, long at)
    {
        if (at < 0 || at + 4 > len) return -100;
        return (blob[at] << 24) | (blob[at + 1] << 16) | (blob[at + 2] << 8) | (blob[at + 3]);
    }
}
