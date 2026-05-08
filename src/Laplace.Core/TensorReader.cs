namespace Laplace.Core;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over the native B19 TensorDecodeService. Native parses
/// the header; managed handle owns the file stream + offset arithmetic +
/// per-dtype lossless decoding into Span&lt;float&gt; / Span&lt;double&gt;
/// destinations.
///
/// Phase 2 / Track B / Service B19. Used by the F5 model decomposer
/// family.
/// </summary>
public sealed class TensorReader : ITensorReader
{
    public ISafetensorsHandle Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (!File.Exists(filePath)) {
            throw new FileNotFoundException("safetensors file not found", filePath);
        }

        var nativeHandle = NativeSafetensors.Open(filePath);
        if (nativeHandle == IntPtr.Zero) {
            throw new InvalidDataException($"failed to open safetensors file: {filePath}");
        }

        try
        {
            var entries           = ReadEntries(nativeHandle);
            var dataSectionOffset = (long) NativeSafetensors.DataSectionOffset(nativeHandle);
            return new SafetensorsHandle(filePath, nativeHandle, entries, dataSectionOffset);
        }
        catch
        {
            NativeSafetensors.Close(nativeHandle);
            throw;
        }
    }

    private static List<SafetensorEntry> ReadEntries(IntPtr nativeHandle)
    {
        var count = (int) NativeSafetensors.EntryCount(nativeHandle);
        var list  = new List<SafetensorEntry>(count);
        unsafe
        {
            for (var i = 0; i < count; i++) {
                var p = NativeSafetensors.EntryAt(nativeHandle, (nuint) i);
                if (p == IntPtr.Zero) {
                    throw new InvalidOperationException($"safetensors entry {i} returned NULL");
                }
                var native = (NativeSafetensors.EntryNative*) p;
                list.Add(MarshalEntry(native));
            }
        }
        return list;
    }

    private static unsafe SafetensorEntry MarshalEntry(NativeSafetensors.EntryNative* native)
    {
        // Name is a null-terminated UTF-8 byte array.
        int nameLen = 0;
        while (nameLen < NativeSafetensors.EntryNative.MaxNameBytes
            && native->name[nameLen] != 0) {
            nameLen++;
        }
        var name = Encoding.UTF8.GetString(native->name, nameLen);

        var shape = new long[native->rank];
        for (var i = 0; i < native->rank; i++) {
            shape[i] = native->shape[i];
        }

        return new SafetensorEntry(
            Name:           name,
            Dtype:          (SafetensorDtype) native->dtype,
            Shape:          shape,
            DataOffset:     (long) native->data_offset,
            DataByteLength: (long) native->data_byte_length);
    }

    private sealed class SafetensorsHandle : ISafetensorsHandle
    {
        private const int IoBufferBytes = 1 << 24;  // 16 MB chunks

        private readonly string                          _filePath;
        private readonly IntPtr                          _nativeHandle;
        private readonly IReadOnlyList<SafetensorEntry>  _entries;
        private readonly Dictionary<string, SafetensorEntry> _byName;
        private readonly long                            _dataSectionOffset;
        private bool                                     _disposed;

        public SafetensorsHandle(
            string                          filePath,
            IntPtr                          nativeHandle,
            IReadOnlyList<SafetensorEntry>  entries,
            long                            dataSectionOffset)
        {
            _filePath          = filePath;
            _nativeHandle      = nativeHandle;
            _entries           = entries;
            _dataSectionOffset = dataSectionOffset;

            _byName = new Dictionary<string, SafetensorEntry>(entries.Count, StringComparer.Ordinal);
            foreach (var e in entries) { _byName[e.Name] = e; }
        }

        public IReadOnlyList<SafetensorEntry> Entries => _entries;

        public SafetensorEntry? Find(string name) =>
            _byName.TryGetValue(name, out var e) ? e : null;

        public void ReadFloat32(SafetensorEntry entry, Span<float> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateDestination(entry, destination.Length);
            using var fs = OpenAtTensor(entry);
            DecodeToFloat32(fs, entry, destination);
        }

        public void ReadFloat64(SafetensorEntry entry, Span<double> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateDestination(entry, destination.Length);
            using var fs = OpenAtTensor(entry);
            DecodeToFloat64(fs, entry, destination);
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            NativeSafetensors.Close(_nativeHandle);
        }

        private static void ValidateDestination(SafetensorEntry entry, long destinationLength)
        {
            var n = entry.ElementCount;
            if (destinationLength < n) {
                throw new ArgumentException(
                    $"destination too small: have {destinationLength}, need {n} for tensor '{entry.Name}'",
                    nameof(destinationLength));
            }
        }

        private FileStream OpenAtTensor(SafetensorEntry entry)
        {
            var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(_dataSectionOffset + entry.DataOffset, SeekOrigin.Begin);
            return fs;
        }

        // -----------------------------------------------------------------
        // Per-dtype decode routines. F32 + F64 are direct LE bit copies;
        // F16, BF16, F8_E4M3, F8_E5M2 do the conversion in managed code.
        // Integer dtypes widen to float / double; BOOL maps 0 → 0.0, 1 → 1.0.
        // -----------------------------------------------------------------

        private static void DecodeToFloat32(FileStream fs, SafetensorEntry entry, Span<float> dst)
        {
            var n = entry.ElementCount;
            switch (entry.Dtype)
            {
                case SafetensorDtype.F32:
                    ReadF32Direct(fs, dst, n);
                    break;
                case SafetensorDtype.F16:
                    ReadF16AsFloat32(fs, dst, n);
                    break;
                case SafetensorDtype.BF16:
                    ReadBF16AsFloat32(fs, dst, n);
                    break;
                case SafetensorDtype.F64:
                    ReadF64AsFloat32(fs, dst, n);
                    break;
                case SafetensorDtype.I8: case SafetensorDtype.U8: case SafetensorDtype.Bool:
                    Read8BitAsFloat32(fs, entry.Dtype, dst, n);
                    break;
                case SafetensorDtype.I16: case SafetensorDtype.U16:
                    Read16BitAsFloat32(fs, entry.Dtype, dst, n);
                    break;
                case SafetensorDtype.I32: case SafetensorDtype.U32:
                    Read32BitAsFloat32(fs, entry.Dtype, dst, n);
                    break;
                case SafetensorDtype.I64: case SafetensorDtype.U64:
                    Read64BitAsFloat32(fs, entry.Dtype, dst, n);
                    break;
                default:
                    throw new NotSupportedException(
                        $"dtype {entry.Dtype} → float32 not implemented for tensor '{entry.Name}'");
            }
        }

        private static void DecodeToFloat64(FileStream fs, SafetensorEntry entry, Span<double> dst)
        {
            var n = entry.ElementCount;
            switch (entry.Dtype)
            {
                case SafetensorDtype.F64:
                    ReadF64Direct(fs, dst, n);
                    break;
                case SafetensorDtype.F32:
                    ReadF32AsFloat64(fs, dst, n);
                    break;
                case SafetensorDtype.F16:
                    ReadF16AsFloat64(fs, dst, n);
                    break;
                case SafetensorDtype.BF16:
                    ReadBF16AsFloat64(fs, dst, n);
                    break;
                case SafetensorDtype.I8: case SafetensorDtype.U8: case SafetensorDtype.Bool:
                    Read8BitAsFloat64(fs, entry.Dtype, dst, n);
                    break;
                case SafetensorDtype.I16: case SafetensorDtype.U16:
                    Read16BitAsFloat64(fs, entry.Dtype, dst, n);
                    break;
                case SafetensorDtype.I32: case SafetensorDtype.U32:
                    Read32BitAsFloat64(fs, entry.Dtype, dst, n);
                    break;
                case SafetensorDtype.I64: case SafetensorDtype.U64:
                    Read64BitAsFloat64(fs, entry.Dtype, dst, n);
                    break;
                default:
                    throw new NotSupportedException(
                        $"dtype {entry.Dtype} → float64 not implemented for tensor '{entry.Name}'");
            }
        }

        // ---- Direct copies ----

        private static void ReadF32Direct(FileStream fs, Span<float> dst, long n)
        {
            var bytesNeeded = n * 4;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 4) {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i, 4));
                    dst[dstIdx++] = BitConverter.UInt32BitsToSingle(bits);
                }
                pos += chunk;
            }
        }

        private static void ReadF64Direct(FileStream fs, Span<double> dst, long n)
        {
            var bytesNeeded = n * 8;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 8) {
                    var bits = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i, 8));
                    dst[dstIdx++] = BitConverter.UInt64BitsToDouble(bits);
                }
                pos += chunk;
            }
        }

        // ---- F16 (IEEE 754 half) ----

        private static void ReadF16AsFloat32(FileStream fs, Span<float> dst, long n)
        {
            var bytesNeeded = n * 2;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 2) {
                    var bits = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i, 2));
                    dst[dstIdx++] = (float) BitConverter.UInt16BitsToHalf(bits);
                }
                pos += chunk;
            }
        }

        private static void ReadF16AsFloat64(FileStream fs, Span<double> dst, long n)
        {
            var bytesNeeded = n * 2;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 2) {
                    var bits = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i, 2));
                    dst[dstIdx++] = (double) BitConverter.UInt16BitsToHalf(bits);
                }
                pos += chunk;
            }
        }

        // ---- BF16 (Brain float; high 16 bits of an F32) ----

        private static void ReadBF16AsFloat32(FileStream fs, Span<float> dst, long n)
        {
            var bytesNeeded = n * 2;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 2) {
                    var bits = (uint) BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i, 2));
                    dst[dstIdx++] = BitConverter.UInt32BitsToSingle(bits << 16);
                }
                pos += chunk;
            }
        }

        private static void ReadBF16AsFloat64(FileStream fs, Span<double> dst, long n)
        {
            var bytesNeeded = n * 2;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 2) {
                    var bits = (uint) BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i, 2));
                    dst[dstIdx++] = (double) BitConverter.UInt32BitsToSingle(bits << 16);
                }
                pos += chunk;
            }
        }

        // ---- F32 → F64 widen / F64 → F32 narrow ----

        private static void ReadF32AsFloat64(FileStream fs, Span<double> dst, long n)
        {
            var bytesNeeded = n * 4;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 4) {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i, 4));
                    dst[dstIdx++] = (double) BitConverter.UInt32BitsToSingle(bits);
                }
                pos += chunk;
            }
        }

        private static void ReadF64AsFloat32(FileStream fs, Span<float> dst, long n)
        {
            var bytesNeeded = n * 8;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 8) {
                    var bits = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i, 8));
                    dst[dstIdx++] = (float) BitConverter.UInt64BitsToDouble(bits);
                }
                pos += chunk;
            }
        }

        // ---- Integer dtype widens ----

        private static void Read8BitAsFloat32(FileStream fs, SafetensorDtype dtype, Span<float> dst, long n)
        {
            var buf = new byte[Math.Min(n, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < n) {
                var chunk = (int) Math.Min(buf.Length, n - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i++) {
                    dst[dstIdx++] = dtype switch {
                        SafetensorDtype.I8   => (sbyte) buf[i],
                        SafetensorDtype.U8   => (float) buf[i],
                        SafetensorDtype.Bool => buf[i] != 0 ? 1.0f : 0.0f,
                        _ => throw new InvalidOperationException($"unexpected dtype {dtype}"),
                    };
                }
                pos += chunk;
            }
        }

        private static void Read8BitAsFloat64(FileStream fs, SafetensorDtype dtype, Span<double> dst, long n)
        {
            var buf = new byte[Math.Min(n, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < n) {
                var chunk = (int) Math.Min(buf.Length, n - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i++) {
                    dst[dstIdx++] = dtype switch {
                        SafetensorDtype.I8   => (sbyte) buf[i],
                        SafetensorDtype.U8   => (double) buf[i],
                        SafetensorDtype.Bool => buf[i] != 0 ? 1.0 : 0.0,
                        _ => throw new InvalidOperationException($"unexpected dtype {dtype}"),
                    };
                }
                pos += chunk;
            }
        }

        private static void Read16BitAsFloat32(FileStream fs, SafetensorDtype dtype, Span<float> dst, long n)
        {
            var bytesNeeded = n * 2;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 2) {
                    var bits = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i, 2));
                    dst[dstIdx++] = dtype == SafetensorDtype.I16
                        ? (float)(short) bits
                        : (float) bits;
                }
                pos += chunk;
            }
        }

        private static void Read16BitAsFloat64(FileStream fs, SafetensorDtype dtype, Span<double> dst, long n)
        {
            var bytesNeeded = n * 2;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 2) {
                    var bits = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i, 2));
                    dst[dstIdx++] = dtype == SafetensorDtype.I16
                        ? (double)(short) bits
                        : (double) bits;
                }
                pos += chunk;
            }
        }

        private static void Read32BitAsFloat32(FileStream fs, SafetensorDtype dtype, Span<float> dst, long n)
        {
            var bytesNeeded = n * 4;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 4) {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i, 4));
                    dst[dstIdx++] = dtype == SafetensorDtype.I32
                        ? (float)(int) bits
                        : (float) bits;
                }
                pos += chunk;
            }
        }

        private static void Read32BitAsFloat64(FileStream fs, SafetensorDtype dtype, Span<double> dst, long n)
        {
            var bytesNeeded = n * 4;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 4) {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i, 4));
                    dst[dstIdx++] = dtype == SafetensorDtype.I32
                        ? (double)(int) bits
                        : (double) bits;
                }
                pos += chunk;
            }
        }

        private static void Read64BitAsFloat32(FileStream fs, SafetensorDtype dtype, Span<float> dst, long n)
        {
            var bytesNeeded = n * 8;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 8) {
                    var bits = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i, 8));
                    dst[dstIdx++] = dtype == SafetensorDtype.I64
                        ? (float)(long) bits
                        : (float) bits;
                }
                pos += chunk;
            }
        }

        private static void Read64BitAsFloat64(FileStream fs, SafetensorDtype dtype, Span<double> dst, long n)
        {
            var bytesNeeded = n * 8;
            var buf = new byte[Math.Min(bytesNeeded, IoBufferBytes)];
            long pos = 0;
            int dstIdx = 0;
            while (pos < bytesNeeded) {
                var chunk = (int) Math.Min(buf.Length, bytesNeeded - pos);
                ReadExactly(fs, buf, 0, chunk);
                for (var i = 0; i < chunk; i += 8) {
                    var bits = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i, 8));
                    dst[dstIdx++] = dtype == SafetensorDtype.I64
                        ? (double)(long) bits
                        : (double) bits;
                }
                pos += chunk;
            }
        }

        private static void ReadExactly(Stream s, byte[] buffer, int offset, int count)
        {
            var got = 0;
            while (got < count) {
                var read = s.Read(buffer, offset + got, count - got);
                if (read == 0) {
                    throw new EndOfStreamException("unexpected EOF reading tensor data");
                }
                got += read;
            }
        }
    }
}
