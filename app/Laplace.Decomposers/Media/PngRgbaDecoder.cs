using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Minimal PNG → planar RGBA decoder for the image lane. Supports 8-bit non-interlaced
/// RGB (color type 2) and RGBA (color type 6). Absent alpha becomes 0xFF. Identity is
/// always the decoded RGBA bytes, never the PNG container bytes.
/// </summary>
public static class PngRgbaDecoder
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool TryDecode(ReadOnlySpan<byte> png, out uint width, out uint height, out byte[] rgba)
    {
        width = 0; height = 0; rgba = Array.Empty<byte>();
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature)) return false;

        uint w = 0, h = 0;
        byte bitDepth = 0, colorType = 0;
        var idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.Slice(pos, 4));
            if (len < 0 || pos + 12 + len > png.Length) return false;
            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, len);
            pos += 12 + len; // len + type + data + crc

            if (type.SequenceEqual("IHDR"u8))
            {
                if (len < 13) return false;
                w = BinaryPrimitives.ReadUInt32BigEndian(data);
                h = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
                bitDepth = data[8];
                colorType = data[9];
                if (data[10] != 0 || data[11] != 0 || data[12] != 0) return false; // compression/filter/interlace
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (w == 0 || h == 0 || bitDepth != 8) return false;
        if (colorType is not (2 or 6)) return false;
        int channels = colorType == 6 ? 4 : 3;
        long rowBytes = 1 + (long)w * channels;
        long rawLen = rowBytes * h;
        if (rawLen > int.MaxValue) return false;

        byte[] raw;
        try
        {
            idat.Position = 0;
            using var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true);
            raw = new byte[(int)rawLen];
            int got = 0;
            while (got < raw.Length)
            {
                int n = zlib.Read(raw, got, raw.Length - got);
                if (n == 0) break;
                got += n;
            }
            if (got != raw.Length) return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        var pixels = new byte[checked((int)((long)w * h * 4))];
        int bpp = channels;
        var prev = new byte[(int)(w * channels)];
        var cur = new byte[(int)(w * channels)];
        for (uint y = 0; y < h; y++)
        {
            int rowOff = (int)(y * rowBytes);
            byte filter = raw[rowOff];
            ReadOnlySpan<byte> filtered = raw.AsSpan(rowOff + 1, (int)(w * channels));
            if (!Unfilter(filter, filtered, prev, cur, bpp)) return false;
            for (uint x = 0; x < w; x++)
            {
                int si = (int)(x * channels);
                int di = (int)((y * w + x) * 4);
                pixels[di] = cur[si];
                pixels[di + 1] = cur[si + 1];
                pixels[di + 2] = cur[si + 2];
                pixels[di + 3] = channels == 4 ? cur[si + 3] : (byte)0xFF;
            }
            (prev, cur) = (cur, prev);
        }

        width = w; height = h; rgba = pixels;
        return true;
    }

    public static async Task<(uint Width, uint Height, byte[] Rgba)?> OpenAsync(
        string path, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (!TryDecode(bytes, out uint w, out uint h, out byte[] rgba))
        {
            Trace.TraceWarning("PngRgbaDecoder: skipping '{0}' — unsupported or corrupt PNG", path);
            return null;
        }
        return (w, h, rgba);
    }

    private static bool Unfilter(byte filter, ReadOnlySpan<byte> src, ReadOnlySpan<byte> prev,
        Span<byte> dst, int bpp)
    {
        if (src.Length != dst.Length) return false;
        switch (filter)
        {
            case 0: // None
                src.CopyTo(dst);
                return true;
            case 1: // Sub
                for (int i = 0; i < dst.Length; i++)
                    dst[i] = (byte)(src[i] + (i >= bpp ? dst[i - bpp] : 0));
                return true;
            case 2: // Up
                for (int i = 0; i < dst.Length; i++)
                    dst[i] = (byte)(src[i] + prev[i]);
                return true;
            case 3: // Average
                for (int i = 0; i < dst.Length; i++)
                {
                    int a = i >= bpp ? dst[i - bpp] : 0;
                    int b = prev[i];
                    dst[i] = (byte)(src[i] + ((a + b) / 2));
                }
                return true;
            case 4: // Paeth
                for (int i = 0; i < dst.Length; i++)
                {
                    int a = i >= bpp ? dst[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    dst[i] = (byte)(src[i] + Paeth(a, b, c));
                }
                return true;
            default:
                return false;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }
}
