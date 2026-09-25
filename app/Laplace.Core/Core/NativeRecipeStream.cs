using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Coarse stream boundary: the native engine consumes input buffers and produces
/// owned tuple batches. No syntax-node or property-value loop crosses this boundary.
/// Instances have one owner; feed and drain must not be used concurrently.
/// </summary>
public sealed class NativeRecipeStream : SafeHandle
{
    private NativeRecipeStream(IntPtr pointer) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(pointer);
    public override bool IsInvalid => handle == IntPtr.Zero;

    public static unsafe NativeRecipeStream Open(ReadOnlySpan<byte> program, Hash128 witness, double trust)
    {
        if (program.IsEmpty) throw new ArgumentException("Recipe program is empty.", nameof(program));
        if (!double.IsFinite(trust) || trust < 0 || trust > 1)
            throw new ArgumentOutOfRangeException(nameof(trust));
        lock (LaplaceCoreGate.Native)
        fixed (byte* bytes = program)
        {
            IntPtr pointer = IntPtr.Zero;
            int result = NativeInterop.RecipeStreamNew(bytes, (nuint)program.Length, &witness, trust, &pointer);
            try
            {
                if (result == 0 && pointer != IntPtr.Zero)
                {
                    var stream = new NativeRecipeStream(pointer);
                    pointer = IntPtr.Zero;
                    return stream;
                }
                throw new InvalidDataException(Error(pointer, result));
            }
            finally
            {
                if (pointer != IntPtr.Zero) NativeInterop.RecipeStreamFree(pointer);
            }
        }
    }

    public unsafe void Feed(ReadOnlySpan<byte> bytes, bool final)
    {
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            fixed (byte* input = bytes)
            {
                int result = NativeInterop.RecipeStreamFeed(handle, input, (nuint)bytes.Length, final ? 1 : 0);
                if (result != 0) throw new InvalidDataException(Error(handle, result));
            }
            GC.KeepAlive(this);
        }
    }

    /// <summary>True when the recipe declares identity tables: the artifact is read once
    /// through <see cref="Prescan"/> before <see cref="Feed"/>.</summary>
    public bool RequiresPrescan
    {
        get
        {
            lock (LaplaceCoreGate.Native)
            {
                ObjectDisposedException.ThrowIf(IsClosed, this);
                bool required = NativeInterop.RecipeStreamRequiresPrescan(handle) == 1;
                GC.KeepAlive(this);
                return required;
            }
        }
    }

    public unsafe void Prescan(ReadOnlySpan<byte> bytes, bool final)
    {
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            fixed (byte* input = bytes)
            {
                int result = NativeInterop.RecipeStreamPrescan(handle, input, (nuint)bytes.Length, final ? 1 : 0);
                if (result != 0) throw new InvalidDataException(Error(handle, result));
            }
            GC.KeepAlive(this);
        }
    }

    /// <summary>The caller owns and must dispose each returned stage.</summary>
    public unsafe IntentStage? Drain(int maximumRows, long maximumBytes, out ulong recordsCompleted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            IntPtr stage = IntPtr.Zero;
            ulong completed = 0;
            int result = NativeInterop.RecipeStreamDrain(handle, (nuint)maximumRows,
                checked((nuint)maximumBytes), &stage, &completed);
            GC.KeepAlive(this);
            if (result < 0)
            {
                if (stage != IntPtr.Zero) NativeInterop.IntentStageFree(stage);
                throw new InvalidDataException(Error(handle, result));
            }
            recordsCompleted = completed;
            return result == 0 ? null : IntentStage.OwnRecipeOutput(stage);
        }
    }

    protected override bool ReleaseHandle()
    {
        lock (LaplaceCoreGate.Native) NativeInterop.RecipeStreamFree(handle);
        return true;
    }

    private static string Error(IntPtr pointer, int result)
    {
        string? message = pointer == IntPtr.Zero ? null
            : Marshal.PtrToStringUTF8(NativeInterop.RecipeStreamError(pointer));
        return string.IsNullOrEmpty(message) ? $"Native recipe stream failed ({result})." : message;
    }
}

public sealed partial class IntentStage
{
    internal static IntentStage OwnRecipeOutput(IntPtr pointer) => pointer != IntPtr.Zero
        ? new IntentStage(pointer)
        : throw new InvalidDataException("Native recipe stream returned an empty stage handle.");
}

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "laplace_recipe_stream_new")]
    internal static partial int RecipeStreamNew(byte* program, nuint size, Hash128* witness,
        double trust, IntPtr* output);
    [LibraryImport(Library, EntryPoint = "laplace_recipe_stream_feed")]
    internal static partial int RecipeStreamFeed(IntPtr stream, byte* bytes, nuint size, int final);
    [LibraryImport(Library, EntryPoint = "laplace_recipe_stream_requires_prescan")]
    internal static partial int RecipeStreamRequiresPrescan(IntPtr stream);
    [LibraryImport(Library, EntryPoint = "laplace_recipe_stream_prescan")]
    internal static partial int RecipeStreamPrescan(IntPtr stream, byte* bytes, nuint size, int final);
    [LibraryImport(Library, EntryPoint = "laplace_recipe_stream_drain")]
    internal static partial int RecipeStreamDrain(IntPtr stream, nuint maximumRows, nuint maximumBytes,
        IntPtr* stage, ulong* completed);
    [LibraryImport(Library, EntryPoint = "laplace_recipe_stream_error")]
    internal static partial IntPtr RecipeStreamError(IntPtr stream);
    [LibraryImport(Library, EntryPoint = "laplace_recipe_stream_free")]
    internal static partial void RecipeStreamFree(IntPtr stream);
}
