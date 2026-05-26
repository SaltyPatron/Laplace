namespace Laplace.Engine.Core;

/// <summary>
/// Managed entry point to the engine's process-wide T0 codepoint perf-cache
/// (engine/core/src/codepoint_table.c). Loads the binary blob once via the
/// engine's mmap loader and exposes the records as a span over the mapped
/// region — the single source of truth shared with the PG extension + the
/// engine state machines (per CLAUDE.md "one source of math truth").
///
/// <para>
/// T0 DB seed uses <c>UnicodeSeed.Compute</c> on UCD/DUCET (sibling of this
/// blob per ADR 0006) — seed does <b>not</b> call <see cref="Load"/>.
/// Runtime clients (TextDecomposer segmentation flags, <see cref="HashComposer"/>
/// call <see cref="Load"/> to avoid per-codepoint DB round-trips.
/// </para>
/// </summary>
public static unsafe class CodepointPerfcache
{
    /// <summary>Load + validate the perf-cache blob at <paramref name="path"/>
    /// and install it as the process-wide table. Throws on failure with the
    /// engine return code decoded.</summary>
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

    /// <summary>Unmap + clear the process-wide table. Safe when not loaded.</summary>
    public static void Unload() => NativeInterop.CodepointTableUnload();

    /// <summary>True iff a perf-cache is currently loaded.</summary>
    public static bool IsLoaded => NativeInterop.CodepointTableIsLoaded() != 0;

    /// <summary>The full records array as a read-only span over the mmap'd
    /// region (record <c>i</c> is codepoint <c>i</c>; length 1,114,112). The
    /// span is valid until <see cref="Unload"/> or a reload. Throws if no
    /// table is loaded.
    ///
    /// <para>This is a <c>ref struct</c> span and cannot live across
    /// <c>await</c>/<c>yield</c>; streaming consumers re-acquire it per
    /// synchronous batch (it is just a view over stable mmap'd memory).</para>
    /// </summary>
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

    /// <summary>Record count of the loaded table (1,114,112) without
    /// materializing the span — usable across async batch boundaries.
    /// Throws if no table is loaded.</summary>
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
