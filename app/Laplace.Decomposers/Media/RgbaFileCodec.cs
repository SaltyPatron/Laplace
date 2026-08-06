using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Canonical on-disk image packaging for the witnessed image ladder (no codec deps):
/// magic <c>RGBA</c>, uint32 LE width, uint32 LE height, then width×height×4 bytes.
/// Absent-alpha RGB sources must already have been expanded with A=0xFF by the producer.
/// </summary>
public static class RgbaFileCodec
{
    public static readonly byte[] Magic = "RGBA"u8.ToArray();

    public static bool TryDecode(ReadOnlySpan<byte> file, out uint width, out uint height, out byte[] rgba)
    {
        width = 0; height = 0; rgba = Array.Empty<byte>();
        if (file.Length < 12) return false;
        if (!file[..4].SequenceEqual(Magic)) return false;
        width = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(4, 4));
        height = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(8, 4));
        if (width == 0 || height == 0) return false;
        long need = (long)width * height * 4;
        if (need > int.MaxValue || file.Length < 12 + need) return false;
        rgba = file.Slice(12, (int)need).ToArray();
        return true;
    }

    public static async Task<(uint Width, uint Height, byte[] Rgba)?> OpenAsync(
        string path, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (!TryDecode(bytes, out uint w, out uint h, out byte[] rgba))
        {
            Trace.TraceWarning("RgbaFileCodec: skipping '{0}' — not a valid RGBA package", path);
            return null;
        }
        return (w, h, rgba);
    }

    public static byte[] Encode(uint width, uint height, ReadOnlySpan<byte> rgba)
    {
        long need = (long)width * height * 4;
        if (rgba.Length < need) throw new ArgumentException("rgba buffer too short");
        var buf = new byte[12 + (int)need];
        Magic.CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), height);
        rgba[..(int)need].CopyTo(buf.AsSpan(12));
        return buf;
    }
}
