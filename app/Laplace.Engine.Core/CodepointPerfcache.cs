namespace Laplace.Engine.Core;

public static unsafe class CodepointPerfcache
{
    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        int rc = NativeInterop.CodepointTableLoadPerfcache(path);
        if (rc != 0)
        {
            string why = rc switch
            {
                -1 => "open/stat/mmap failure (missing or unreadable file)",
                -2 => "bad magic / unsupported format version",
                -3 => "record count / size mismatch",
                -4 => "body CRC mismatch (corrupt blob)",
                _  => "unknown error",
            };
            throw new InvalidOperationException(
                $"codepoint_table_load_perfcache(\"{path}\") failed (rc={rc}): {why}");
        }
    }

    public static void Unload() => NativeInterop.CodepointTableUnload();

    public static bool IsLoaded => NativeInterop.CodepointTableIsLoaded() != 0;

    public static ReadOnlySpan<CodepointRecord> Records
    {
        get
        {
            CodepointRecord* recs;
            ulong count;
            int rc = NativeInterop.CodepointTableRecords(&recs, &count);
            if (rc != 0)
                throw new InvalidOperationException(
                    "codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
            return new ReadOnlySpan<CodepointRecord>(recs, checked((int)count));
        }
    }

    public static int Count
    {
        get
        {
            CodepointRecord* recs;
            ulong count;
            int rc = NativeInterop.CodepointTableRecords(&recs, &count);
            if (rc != 0)
                throw new InvalidOperationException(
                    "codepoint perf-cache not loaded; call CodepointPerfcache.Load first");
            return checked((int)count);
        }
    }
}
