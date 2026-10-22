using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe class UnicodeSeed
{
    public const int CodepointCount = 1_112_064;

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
    /// Retain source XML for semantic ingestion while reading scalar records from
    /// the shared Native floor. This does not regenerate or seed a second floor.
    /// </summary>
    public static UnicodeSeedSnapshot OpenSnapshot(string ucdxmlPath, string ducetPath) =>
        UnicodeSeedSnapshot.Open(ucdxmlPath, ducetPath);
}

/// <summary>
/// Source XML bytes plus a read-only view of the shared Native scalar mapping.
/// </summary>
public sealed unsafe class UnicodeSeedSnapshot : SafeHandle
{
    private UnicodeSeedSnapshot() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    internal static UnicodeSeedSnapshot Open(string ucdxmlPath, string ducetPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ucdxmlPath);
        ArgumentException.ThrowIfNullOrEmpty(ducetPath);
        CodepointPerfcache.LoadDefault();
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
    /// Opens the exact inflated XML bytes retained by this source snapshot. The
    /// returned stream holds a native snapshot reference and performs bounded memory
    /// copies; it never reopens the physical UCD artifact.
    /// </summary>
    public Stream OpenUcdXmlStream()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        return new SnapshotXmlStream(this);
    }

    private sealed class SnapshotXmlStream : Stream
    {
        private readonly UnicodeSeedSnapshot _owner;
        private readonly IntPtr _snapshot;
        private nuint _position;
        private bool _released;

        internal SnapshotXmlStream(UnicodeSeedSnapshot owner)
        {
            _owner = owner;
            bool added = false;
            try
            {
                owner.DangerousAddRef(ref added);
                if (!added) throw new ObjectDisposedException(nameof(UnicodeSeedSnapshot));
                _snapshot = owner.DangerousGetHandle();
            }
            catch
            {
                if (added) owner.DangerousRelease();
                throw;
            }
        }

        public override bool CanRead => !_released;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => checked((long)_position);
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            if (buffer.IsEmpty) return 0;
            nuint copied = 0;
            int rc;
            unsafe
            {
                fixed (byte* destination = buffer)
                    rc = NativeInterop.UnicodeSeedSnapshotXmlCopy(
                        _snapshot, _position, destination,
                        checked((nuint)buffer.Length), &copied);
            }
            if (rc != 0 || copied > (nuint)buffer.Length)
                throw new InvalidDataException(
                    $"Unicode source snapshot XML read failed at {_position} (rc={rc}, copied={copied}).");
            _position = checked(_position + copied);
            return checked((int)copied);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        protected override void Dispose(bool disposing)
        {
            if (!_released)
            {
                _released = true;
                _owner.DangerousRelease();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
