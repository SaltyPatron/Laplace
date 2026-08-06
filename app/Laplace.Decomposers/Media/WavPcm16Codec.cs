using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Minimal WAV reader: PCM signed 16-bit, mono only (channel is a partition).
/// Stereo / float / compressed formats are skipped with a warning.
/// </summary>
public static class WavPcm16Codec
{
    public static bool TryDecode(ReadOnlySpan<byte> file, out int sampleRate, out short[] pcm)
    {
        sampleRate = 0;
        pcm = Array.Empty<short>();
        if (file.Length < 44) return false;
        if (!file[..4].SequenceEqual("RIFF"u8)) return false;
        if (!file.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;

        int pos = 12;
        ushort format = 0, channels = 0, bits = 0;
        int rate = 0;
        ReadOnlySpan<byte> data = default;
        while (pos + 8 <= file.Length)
        {
            var id = file.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(pos + 4, 4));
            pos += 8;
            if (size < 0 || pos + size > file.Length) return false;
            var chunk = file.Slice(pos, size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16) return false;
                format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(14));
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = chunk;
            }
            pos += size + (size & 1); // word align
        }

        if (format != 1 || channels != 1 || bits != 16 || rate <= 0 || data.Length < 2)
            return false;
        int n = data.Length / 2;
        var samples = new short[n];
        for (int i = 0; i < n; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
        sampleRate = rate;
        pcm = samples;
        return true;
    }

    public static async Task<(int SampleRate, short[] Pcm)?> OpenAsync(
        string path, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (!TryDecode(bytes, out int rate, out short[] pcm))
        {
            Trace.TraceWarning(
                "WavPcm16Codec: skipping '{0}' — need PCM16 mono WAV", path);
            return null;
        }
        return (rate, pcm);
    }
}
