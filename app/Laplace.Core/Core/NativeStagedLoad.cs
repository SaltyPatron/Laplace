using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>The staged load's native streaming operations (engine/core staged_load.cpp).</summary>
public enum StagedLoadOperation
{
    /// <summary>Claims sorted by id in; one merged claim per id out.</summary>
    MergeClaims,
    /// <summary>New evidence sorted by cell and witness, with prior standing, in; novel
    /// cells (output 0) and cells with prior standing (output 1) out.</summary>
    Score,
}

/// <summary>
/// One native pass over a binary COPY stream read out of staging. Input arrives in slices
/// of any size; output is taken in pieces and written straight back to PostgreSQL. The
/// native side holds one claim or one cell, never the source. One owner.
/// </summary>
public sealed class NativeStagedLoad : SafeHandle
{
    private readonly StagedLoadOperation _operation;

    private NativeStagedLoad(IntPtr pointer, StagedLoadOperation operation)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        _operation = operation;
        SetHandle(pointer);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public static NativeStagedLoad Create(StagedLoadOperation operation)
    {
        IntPtr pointer = operation == StagedLoadOperation.MergeClaims
            ? NativeInterop.StagedClaimsNew()
            : NativeInterop.StagedScoreNew();
        return pointer == IntPtr.Zero
            ? throw new OutOfMemoryException("staged load operation could not be created")
            : new NativeStagedLoad(pointer, operation);
    }

    public unsafe void Feed(ReadOnlySpan<byte> bytes, bool final)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        int result;
        fixed (byte* p = bytes)
            result = _operation == StagedLoadOperation.MergeClaims
                ? NativeInterop.StagedClaimsFeed(handle, p, (nuint)bytes.Length, final ? 1 : 0)
                : NativeInterop.StagedScoreFeed(handle, p, (nuint)bytes.Length, final ? 1 : 0);
        GC.KeepAlive(this);
        if (result != 0) throw new InvalidDataException(Error());
    }

    /// <summary>Copies the output produced since the last take into <paramref name="into"/>
    /// and releases it natively. Output 1 exists only for scoring.</summary>
    public unsafe void TakeOutput(int output, System.Buffers.ArrayBufferWriter<byte> into)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        nuint length;
        byte* p = _operation == StagedLoadOperation.MergeClaims
            ? NativeInterop.StagedClaimsOutput(handle, &length)
            : NativeInterop.StagedScoreOutput(handle, output, &length);
        if (length > 0)
            new ReadOnlySpan<byte>(p, checked((int)length)).CopyTo(into.GetSpan(checked((int)length)));
        if (length > 0) into.Advance(checked((int)length));
        if (_operation == StagedLoadOperation.MergeClaims) NativeInterop.StagedClaimsConsume(handle);
        else NativeInterop.StagedScoreConsume(handle, output);
        GC.KeepAlive(this);
    }

    /// <summary>Merge: rows in and rows out. Score: cells folded and games folded.</summary>
    public unsafe (ulong First, ulong Second) Counts
    {
        get
        {
            ulong a = 0, b = 0;
            if (_operation == StagedLoadOperation.MergeClaims) NativeInterop.StagedClaimsCounts(handle, &a, &b);
            else NativeInterop.StagedScoreCounts(handle, &a, &b);
            GC.KeepAlive(this);
            return (a, b);
        }
    }

    protected override bool ReleaseHandle()
    {
        if (_operation == StagedLoadOperation.MergeClaims) NativeInterop.StagedClaimsFree(handle);
        else NativeInterop.StagedScoreFree(handle);
        return true;
    }

    private string Error()
    {
        IntPtr message = _operation == StagedLoadOperation.MergeClaims
            ? NativeInterop.StagedClaimsError(handle)
            : NativeInterop.StagedScoreError(handle);
        string? text = Marshal.PtrToStringUTF8(message);
        return string.IsNullOrEmpty(text) ? "Native staged load failed." : text;
    }
}

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "laplace_staged_claims_new")]
    internal static partial IntPtr StagedClaimsNew();
    [LibraryImport(Library, EntryPoint = "laplace_staged_claims_free")]
    internal static partial void StagedClaimsFree(IntPtr merge);
    [LibraryImport(Library, EntryPoint = "laplace_staged_claims_error")]
    internal static partial IntPtr StagedClaimsError(IntPtr merge);
    [LibraryImport(Library, EntryPoint = "laplace_staged_claims_feed")]
    internal static partial int StagedClaimsFeed(IntPtr merge, byte* bytes, nuint length, int final);
    [LibraryImport(Library, EntryPoint = "laplace_staged_claims_output")]
    internal static partial byte* StagedClaimsOutput(IntPtr merge, nuint* length);
    [LibraryImport(Library, EntryPoint = "laplace_staged_claims_consume")]
    internal static partial void StagedClaimsConsume(IntPtr merge);
    [LibraryImport(Library, EntryPoint = "laplace_staged_claims_counts")]
    internal static partial void StagedClaimsCounts(IntPtr merge, ulong* rowsIn, ulong* rowsOut);
    [LibraryImport(Library, EntryPoint = "laplace_staged_score_new")]
    internal static partial IntPtr StagedScoreNew();
    [LibraryImport(Library, EntryPoint = "laplace_staged_score_free")]
    internal static partial void StagedScoreFree(IntPtr score);
    [LibraryImport(Library, EntryPoint = "laplace_staged_score_error")]
    internal static partial IntPtr StagedScoreError(IntPtr score);
    [LibraryImport(Library, EntryPoint = "laplace_staged_score_feed")]
    internal static partial int StagedScoreFeed(IntPtr score, byte* bytes, nuint length, int final);
    [LibraryImport(Library, EntryPoint = "laplace_staged_score_output")]
    internal static partial byte* StagedScoreOutput(IntPtr score, int standing, nuint* length);
    [LibraryImport(Library, EntryPoint = "laplace_staged_score_consume")]
    internal static partial void StagedScoreConsume(IntPtr score, int standing);
    [LibraryImport(Library, EntryPoint = "laplace_staged_score_counts")]
    internal static partial void StagedScoreCounts(IntPtr score, ulong* cells, ulong* games);
}
