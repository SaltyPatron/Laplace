namespace Laplace.Engine.Core;

/// <summary>
/// Managed wrapper for the C engine's <c>laplace_unicode_seed_compute</c> —
/// the single function that parses UCDXML + DUCET and produces the 1,114,112
/// per-codepoint records the substrate seed needs. Same compiled function the
/// perf-cache emitter calls, so the C# DB seed and the on-disk blob are
/// byte-identical siblings of one engine pass. C# marshals; never re-derives.
/// </summary>
public static unsafe class UnicodeSeed
{
    public const int CodepointCount = 0x110000;   // 1,114,112

    /// <summary>Computes all 1,114,112 codepoint records from the given UCD
    /// source files into <paramref name="outRecords"/>. Throws on failure.</summary>
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

    /// <summary>Allocating convenience: returns a fresh record array.</summary>
    public static CodepointRecord[] Compute(string ucdxmlPath, string ducetPath)
    {
        var buf = new CodepointRecord[CodepointCount];
        Compute(ucdxmlPath, ducetPath, buf);
        return buf;
    }
}
