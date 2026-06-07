namespace Laplace.Engine.Core;

public static unsafe class UnicodeSeed
{
    public const int CodepointCount = 0x110000;

    public static void Compute(string ucdxmlPath, string ducetPath, Span<CodepointRecord> outRecords)
    {
        ArgumentException.ThrowIfNullOrEmpty(ucdxmlPath);
        ArgumentException.ThrowIfNullOrEmpty(ducetPath);
        if (outRecords.Length < CodepointCount)
            throw new ArgumentException($"buffer must hold at least {CodepointCount} records", nameof(outRecords));
        fixed (CodepointRecord* p = outRecords)
        {
            int rc = NativeInterop.UnicodeSeedCompute(ucdxmlPath, ducetPath, p, (nuint)outRecords.Length);
            if (rc != 0) throw new InvalidOperationException(
                $"laplace_unicode_seed_compute(\"{ucdxmlPath}\", \"{ducetPath}\") returned {rc}");
        }
    }

    public static CodepointRecord[] Compute(string ucdxmlPath, string ducetPath)
    {
        var buf = new CodepointRecord[CodepointCount];
        Compute(ucdxmlPath, ducetPath, buf);
        return buf;
    }
}
