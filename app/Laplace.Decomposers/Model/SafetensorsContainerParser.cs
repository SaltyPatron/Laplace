using System.Runtime.InteropServices;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Valet over the NATIVE safetensors header parser
/// (engine/synthesis/src/safetensors_parser.cpp). This type resolves files and shapes
/// the result into <see cref="TensorReference"/> records; it does not parse the
/// container itself — one parser for the format, in C++, per the layer law.
/// </summary>
public sealed class SafetensorsContainerParser
{
    public sealed class TensorReference
    {
        public required string Name { get; init; }
        public required string Dtype { get; init; }
        public required int[] Shape { get; init; }
        public required long DataStart { get; init; }
        public required long DataEnd { get; init; }
        public required long HeaderBytes { get; init; }
        public string FilePath { get; set; } = "";

        public long AbsoluteDataStart => HeaderBytes + DataStart;
        public long AbsoluteDataEnd => HeaderBytes + DataEnd;
        public long DataLength => DataEnd - DataStart;
    }

    public static IReadOnlyList<TensorReference> ParseModel(string modelDir)
    {
        var files = Directory.GetFiles(modelDir, "*.safetensors")
                             .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw new FileNotFoundException(
                $"no .safetensors in snapshot dir: {modelDir} "
                + "(safetensor witnesses require config.json + tokenizer.json + weight blobs — not self-contained like GGUF)");
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

    /// <summary>
    /// Reads the container header ([u64 LE json length][json]) and hands it to the
    /// native parser. Only the header is read — tensor data is never pulled here.
    /// </summary>
    public static IReadOnlyList<TensorReference> ParseHeader(Stream stream)
    {
        Span<byte> lenBuf = stackalloc byte[8];
        if (stream.Read(lenBuf) < 8)
            throw new InvalidDataException("safetensors: truncated header-length field");

        long headerJsonLen = 0;
        for (int i = 0; i < 8; i++) headerJsonLen |= ((long)lenBuf[i]) << (8 * i);
        if (headerJsonLen <= 0 || headerJsonLen > 256 * 1024 * 1024)
            throw new InvalidDataException($"safetensors: implausible header length {headerJsonLen}");

        // The native parser takes the length prefix and the JSON as one buffer.
        byte[] header = new byte[8 + headerJsonLen];
        lenBuf.CopyTo(header);
        int total = 8;
        while (total < header.Length)
        {
            int n = stream.Read(header, total, header.Length - total);
            if (n == 0) throw new InvalidDataException("safetensors: truncated header JSON");
            total += n;
        }

        IntPtr h;
        unsafe
        {
            fixed (byte* p = header) h = SynInterop.SafetensorsParseHeader(p, (nuint)header.Length);
        }
        if (h == IntPtr.Zero)
            throw new InvalidDataException(
                "safetensors: header does not describe its own tensors — malformed JSON, or an entry " +
                "missing dtype/shape/data_offsets, or reversed offsets. Refusing to read it.");

        try
        {
            long headerBytes = SynInterop.SafetensorsHeaderBytes(h);
            int count = SynInterop.SafetensorsTensorCount(h);
            var refs = new List<TensorReference>(count);

            for (int i = 0; i < count; i++)
            {
                int rank = SynInterop.SafetensorsTensorRank(h, i);
                var shape = new int[rank];
                for (int a = 0; a < rank; a++)
                    shape[a] = checked((int)SynInterop.SafetensorsTensorDim(h, i, a));

                refs.Add(new TensorReference
                {
                    Name = Marshal.PtrToStringUTF8(SynInterop.SafetensorsTensorName(h, i))!,
                    Dtype = Marshal.PtrToStringUTF8(SynInterop.SafetensorsTensorDtype(h, i))!,
                    Shape = shape,
                    DataStart = SynInterop.SafetensorsTensorDataStart(h, i),
                    DataEnd = SynInterop.SafetensorsTensorDataEnd(h, i),
                    HeaderBytes = headerBytes,
                });
            }

            // Native already returns data-offset order; nothing to re-sort.
            return refs;
        }
        finally
        {
            SynInterop.SafetensorsHeaderFree(h);
        }
    }
}
