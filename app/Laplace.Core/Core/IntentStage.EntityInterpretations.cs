namespace Laplace.Engine.Core;

public sealed partial class IntentStage
{
    /// <summary>True when the auxiliary stream is the complete observed facet set.
    /// A legacy E/P/A-only import leaves this false; its entity tuples remain the
    /// compatibility source of interpretations until native completion seeds them.</summary>
    public bool EntityInterpretationsComplete
    {
        get
        {
            lock (LaplaceCoreGate.Native)
            {
                ThrowIfDisposed();
                bool complete = NativeInterop.IntentStageEntityInterpretationsComplete(handle) != 0;
                GC.KeepAlive(this);
                return complete;
            }
        }
    }

    public int EntityInterpretationCount
    {
        get
        {
            lock (LaplaceCoreGate.Native)
            {
                ThrowIfDisposed();
                int count = checked((int)NativeInterop.IntentStageEntityInterpretationCount(handle));
                GC.KeepAlive(this);
                return count;
            }
        }
    }

    /// <summary>Record an observed facet without changing E/P/A transport or the
    /// historical semantic digest. Admission binds the complete facet set separately.</summary>
    public unsafe void AddEntityInterpretation(
        Hash128 id, short tier, Hash128 typeId, Hash128? firstObservedBy)
    {
        if (tier < 0 || tier > 255) throw new ArgumentOutOfRangeException(nameof(tier));
        lock (LaplaceCoreGate.Native)
        {
            ThrowIfDisposed();
            Hash128 source = firstObservedBy ?? default;
            int status = NativeInterop.IntentStageAddEntityInterpretation(
                handle, &id, tier, &typeId, firstObservedBy.HasValue ? &source : null);
            GC.KeepAlive(this);
            if (status != 0)
                throw new InvalidOperationException($"native entity interpretation staging failed: {status}");
        }
    }

    /// <summary>Borrow the native four-column facet framing. The stage must remain
    /// alive and unmodified until the reader finishes, exactly as for TupleBuffer.</summary>
    public unsafe (IntPtr Pointer, long Length) EntityInterpretationTupleBuffer()
    {
        lock (LaplaceCoreGate.Native)
        {
            ThrowIfDisposed();
            nuint length = 0;
            byte* pointer = NativeInterop.IntentStageEntityInterpretationTuplePtr(handle, &length);
            GC.KeepAlive(this);
            return ((IntPtr)pointer, checked((long)length));
        }
    }

    public unsafe byte[] EmitEntityInterpretationTuples()
    {
        lock (LaplaceCoreGate.Native)
        {
            ThrowIfDisposed();
            nuint length = 0;
            byte* pointer = NativeInterop.IntentStageEntityInterpretationTuplePtr(handle, &length);
            var bytes = new ReadOnlySpan<byte>(pointer, checked((int)length)).ToArray();
            GC.KeepAlive(this);
            return bytes;
        }
    }

    /// <summary>Replace the complete auxiliary stream, including an explicitly
    /// empty stream, after native framing validation. E/P/A bytes are unchanged.</summary>
    public unsafe void ImportEntityInterpretations(ReadOnlySpan<byte> tuples)
    {
        lock (LaplaceCoreGate.Native)
        fixed (byte* pointer = tuples)
        {
            ThrowIfDisposed();
            int status = NativeInterop.IntentStageImportEntityInterpretations(
                handle, pointer, (nuint)tuples.Length);
            GC.KeepAlive(this);
            if (status != 0)
                throw new InvalidOperationException($"native entity interpretation import failed: {status}");
        }
    }
}
