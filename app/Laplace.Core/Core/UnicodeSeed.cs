using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe class UnicodeSeed
{
    public const int CodepointCount = 0x110000;

    /// <summary>Read/decompress and parse one UCD XML artifact to completion.</summary>
    public static void ValidateUcdXml(string ucdxmlPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ucdxmlPath);
        int rc = NativeInterop.UnicodeSeedValidateUcdXml(ucdxmlPath);
        if (rc != 0)
            throw new InvalidOperationException(
                $"laplace_unicode_seed_validate_ucdxml(\"{ucdxmlPath}\") returned {rc}");
    }

    /// <summary>
    /// Parse the authoritative UCD XML + DUCET inputs into immutable source-ingest
    /// working state. This snapshot is not the installed T0 perfcache and never maps it.
    /// </summary>
    public static UnicodeSeedSnapshot OpenSnapshot(string ucdxmlPath, string ducetPath) =>
        UnicodeSeedSnapshot.Open(ucdxmlPath, ducetPath);
}

/// <summary>
/// Immutable native Unicode source snapshot used only while admitting the Unicode floor.
/// PostgreSQL receives rows staged from this object; the runtime perfcache is downstream
/// derived state and is deliberately absent from this API.
/// </summary>
public sealed unsafe class UnicodeSeedSnapshot : SafeHandle
{
    private UnicodeSeedSnapshot() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    internal static UnicodeSeedSnapshot Open(string ucdxmlPath, string ducetPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ucdxmlPath);
        ArgumentException.ThrowIfNullOrEmpty(ducetPath);
        IntPtr native = IntPtr.Zero;
        int rc = NativeInterop.UnicodeSeedSnapshotOpen(ucdxmlPath, ducetPath, &native);
        if (rc != 0 || native == IntPtr.Zero)
            throw new InvalidOperationException(
                $"laplace_unicode_seed_snapshot_open failed (rc={rc}) for UCDXML '{ucdxmlPath}' and DUCET '{ducetPath}'.");

        var snapshot = new UnicodeSeedSnapshot();
        snapshot.SetHandle(native);
        if (snapshot.Count != UnicodeSeed.CodepointCount)
        {
            snapshot.Dispose();
            throw new InvalidOperationException(
                $"Unicode source snapshot has {snapshot.Count} records; expected {UnicodeSeed.CodepointCount}.");
        }
        return snapshot;
    }

    protected override bool ReleaseHandle()
    {
        NativeInterop.UnicodeSeedSnapshotFree(handle);
        return true;
    }

    public int Count
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            nuint count = NativeInterop.UnicodeSeedSnapshotCount(handle);
            if (count > int.MaxValue)
                throw new InvalidOperationException($"Unicode source snapshot count {count} exceeds managed indexing.");
            return checked((int)count);
        }
    }

    public CodepointRecord RecordAt(int index)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= Count) throw new ArgumentOutOfRangeException(nameof(index));

        bool addRef = false;
        try
        {
            DangerousAddRef(ref addRef);
            CodepointRecord record = default;
            int rc = NativeInterop.UnicodeSeedSnapshotCopyRecord(
                DangerousGetHandle(), checked((nuint)index), &record);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"laplace_unicode_seed_snapshot_copy_record({index}) failed (rc={rc}).");
            return record;
        }
        finally
        {
            if (addRef) DangerousRelease();
        }
    }

    /// <summary>
    /// One managed/native crossing stages a complete contiguous floor range. Native owns
    /// entity/physicality tuple construction; managed code only records the source span.
    /// </summary>
    public void StageRange(IntentStage stage, int first, int count, Hash128 sourceId)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if ((long)first + count > Count)
            throw new ArgumentOutOfRangeException(nameof(count));

        int firstEntity = stage.EntityCount;
        int firstPhysicality = stage.PhysicalityCount;
        bool addRef = false;
        try
        {
            DangerousAddRef(ref addRef);
            Hash128 source = sourceId;
            int rc = NativeInterop.UnicodeSeedSnapshotStage(
                DangerousGetHandle(), checked((nuint)first), checked((nuint)count),
                stage.DangerousNativeHandle, &source);
            GC.KeepAlive(stage);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"laplace_unicode_seed_snapshot_stage(first={first}, count={count}) failed (rc={rc}).");
        }
        finally
        {
            if (addRef) DangerousRelease();
        }

        int entityCount = stage.EntityCount - firstEntity;
        int physicalityCount = stage.PhysicalityCount - firstPhysicality;
        if (entityCount != count || physicalityCount != count)
            throw new InvalidOperationException(
                $"Unicode source stage emitted {entityCount} entities/{physicalityCount} physicalities for {count} codepoints.");

        stage.RecordPhysicalitySourceRange(firstPhysicality, physicalityCount, sourceId);
    }
}
