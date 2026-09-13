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

    /// <summary>
    /// Compute the database tier-0 identity/UCA geometry from the one physical DUCET
    /// artifact. Segmentation flags stay zero because they belong to UCD XML and are not
    /// consumed by the database tier-0 physicality rows.
    /// </summary>
    public static CodepointRecord[] ComputeDucetGeometry(string ducetPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ducetPath);
        var records = new CodepointRecord[CodepointCount];
        fixed (CodepointRecord* p = records)
        {
            int rc = NativeInterop.UnicodeSeedComputeDucet(
                ducetPath, p, (nuint)records.Length);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"laplace_unicode_seed_compute_ducet(\"{ducetPath}\") returned {rc}");
        }
        return records;
    }

    /// <summary>Read/decompress and parse one UCD XML artifact to completion.</summary>
    public static void ValidateUcdXml(string ucdxmlPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ucdxmlPath);
        int rc = NativeInterop.UnicodeSeedValidateUcdXml(ucdxmlPath);
        if (rc != 0)
            throw new InvalidOperationException(
                $"laplace_unicode_seed_validate_ucdxml(\"{ucdxmlPath}\") returned {rc}");
    }
}
