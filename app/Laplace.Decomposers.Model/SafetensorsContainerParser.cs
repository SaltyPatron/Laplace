using System.Text.Json;

namespace Laplace.Decomposers.Model;

public sealed class SafetensorsContainerParser
{
    public sealed class TensorReference
    {
        public required string   Name        { get; init; }
        public required string   Dtype       { get; init; }
        public required int[]    Shape       { get; init; }
        public required long     DataStart   { get; init; }
        public required long     DataEnd     { get; init; }
        public required long     HeaderBytes { get; init; }
        public string FilePath { get; set; } = "";

        public long AbsoluteDataStart => HeaderBytes + DataStart;
        public long AbsoluteDataEnd   => HeaderBytes + DataEnd;
        public long DataLength        => DataEnd - DataStart;
    }

    public static IReadOnlyList<TensorReference> ParseModel(string modelDir)
    {
        var files = Directory.GetFiles(modelDir, "*.safetensors")
                             .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw new FileNotFoundException($"no .safetensors found in model dir: {modelDir}");
        var all = new List<TensorReference>();
        foreach (var f in files) all.AddRange(ParseHeader(f));
        all.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.FilePath, b.FilePath);
            return c != 0 ? c : a.DataStart.CompareTo(b.DataStart);
        });
        return all;
    }

    public static IReadOnlyList<TensorReference> ParseHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                      bufferSize: 4096, useAsync: false);
        var refs = ParseHeader(fs);
        foreach (var r in refs) r.FilePath = path;
        return refs;
    }

    public static IReadOnlyList<TensorReference> ParseHeader(Stream stream)
    {
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
