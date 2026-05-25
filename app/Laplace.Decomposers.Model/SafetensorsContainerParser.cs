using System.Text.Json;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Static dissection of the safetensors container format per ADR 0055.
/// Reads ONLY the header (first 8 + header_len bytes); never loads tensor data.
/// </summary>
public sealed class SafetensorsContainerParser
{
    /// <summary>Logical view of one tensor in the container.</summary>
    public sealed class TensorReference
    {
        public required string   Name        { get; init; }
        public required string   Dtype       { get; init; }  // "BF16", "F32", etc.
        public required int[]    Shape       { get; init; }
        public required long     DataStart   { get; init; }  // byte offset from data section start
        public required long     DataEnd     { get; init; }
        public required long     HeaderBytes { get; init; }  // 8 + header_json_len, for absolute seek

        public long AbsoluteDataStart => HeaderBytes + DataStart;
        public long AbsoluteDataEnd   => HeaderBytes + DataEnd;
        public long DataLength        => DataEnd - DataStart;
    }

    /// <summary>
    /// Parse the safetensors header from <paramref name="path"/>.
    /// Returns tensor references ordered by data_offsets[0] (file order).
    /// </summary>
    public static IReadOnlyList<TensorReference> ParseHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                      bufferSize: 4096, useAsync: false);
        return ParseHeader(fs);
    }

    public static IReadOnlyList<TensorReference> ParseHeader(Stream stream)
    {
        /* First 8 bytes = uint64_le: byte length of the header JSON. */
        Span<byte> lenBuf = stackalloc byte[8];
        int read = stream.Read(lenBuf);
        if (read < 8) throw new InvalidDataException("safetensors: truncated header-length field");

        long headerJsonLen = 0;
        for (int i = 0; i < 8; i++) headerJsonLen |= ((long)lenBuf[i]) << (8 * i);
        if (headerJsonLen <= 0 || headerJsonLen > 256 * 1024 * 1024)
            throw new InvalidDataException($"safetensors: implausible header length {headerJsonLen}");

        byte[] jsonBytes = new byte[headerJsonLen];
        int total = 0;
        while (total < jsonBytes.Length)
        {
            int n = stream.Read(jsonBytes, total, jsonBytes.Length - total);
            if (n == 0) throw new InvalidDataException("safetensors: truncated header JSON");
            total += n;
        }

        long headerBytes = 8 + headerJsonLen;

        using var doc = JsonDocument.Parse(jsonBytes);
        var refs = new List<TensorReference>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            /* Skip the special "__metadata__" key */
            if (prop.Name == "__metadata__") continue;

            var entry = prop.Value;
            string dtype = entry.GetProperty("dtype").GetString() ?? "BF16";

            var shapeArr = entry.GetProperty("shape");
            int[] shape = new int[shapeArr.GetArrayLength()];
            int si = 0;
            foreach (var dim in shapeArr.EnumerateArray())
                shape[si++] = dim.GetInt32();

            var offsets = entry.GetProperty("data_offsets");
            long dataStart = offsets[0].GetInt64();
            long dataEnd   = offsets[1].GetInt64();

            refs.Add(new TensorReference
            {
                Name        = prop.Name,
                Dtype       = dtype,
                Shape       = shape,
                DataStart   = dataStart,
                DataEnd     = dataEnd,
                HeaderBytes = headerBytes,
            });
        }

        refs.Sort((a, b) => a.DataStart.CompareTo(b.DataStart));
        return refs;
    }
}
