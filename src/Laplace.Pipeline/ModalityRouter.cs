namespace Laplace.Pipeline;

using System;
using System.Collections.Generic;
using System.IO;

using Laplace.Decomposers.Abstractions;

/// <summary>
/// F3 IModalityRouter. Dispatches an artifact to its modality decomposer
/// by inspecting (in order): file extension → magic bytes → fallback. The
/// returned modality identifier is the canonical name string registered
/// as a substrate concept entity (composition of its codepoint LINESTRING)
/// — text, image, audio, video, code, structured, math, model, etc.
///
/// Per substrate invariants: the routing table is data, not code structure.
/// New modality decomposers slot in by registering their canonical names;
/// no schema changes, no enum extension.
/// </summary>
public sealed class ModalityRouter : IModalityRouter
{
    /// <summary>Fallback when no modality can be detected. Substrate
    /// stores the artifact opaquely with a "semantic decomposition
    /// unavailable" flag.</summary>
    public const string UnknownModality = "opaque";

    /// <summary>Canonical modality name → set of file extensions (lower-case
    /// including the leading dot). Frozen at v1.0; new modalities ship with
    /// new entries.</summary>
    private static readonly Dictionary<string, string[]> ExtensionMap = new(StringComparer.Ordinal)
    {
        // Text + structured.
        ["text"]       = new[] { ".txt", ".md", ".rst", ".log", ".tsv", ".csv" },
        ["structured"] = new[] { ".json", ".yaml", ".yml", ".toml", ".xml", ".ini", ".conf" },
        ["code"]       = new[] { ".cs", ".py", ".js", ".ts", ".jsx", ".tsx", ".rs", ".go", ".java", ".cpp", ".cc", ".c", ".h", ".hpp", ".rb", ".swift", ".kt", ".scala", ".lua", ".r", ".php", ".pl", ".sh", ".ps1", ".bash", ".zsh", ".sql" },
        ["math"]       = new[] { ".tex", ".latex", ".mathml" },
        ["web"]        = new[] { ".html", ".htm", ".xhtml" },

        // Image / audio / video — semantic decomposers (NOT byte blobs).
        ["image"]      = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".svg", ".ico" },
        ["audio"]      = new[] { ".wav", ".flac", ".mp3", ".ogg", ".opus", ".m4a", ".aac", ".aiff", ".au" },
        ["video"]      = new[] { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".flv", ".wmv" },

        // Domain-specific.
        ["geo"]        = new[] { ".geojson", ".kml", ".kmz", ".shp", ".gpx" },
        ["cad"]        = new[] { ".step", ".stp", ".iges", ".igs", ".dxf", ".dwg", ".stl" },
        ["network"]    = new[] { ".pcap", ".pcapng" },
        ["music"]      = new[] { ".mid", ".midi", ".musicxml", ".mxl" },
        ["bio"]        = new[] { ".fasta", ".fa", ".fastq", ".fq", ".pdb", ".sam", ".bam", ".vcf", ".gff" },
        ["compressed"] = new[] { ".zip", ".tar", ".gz", ".bz2", ".xz", ".7z", ".rar" },

        // AI model artifacts.
        ["model"]      = new[] { ".safetensors", ".onnx", ".gguf", ".bin", ".pt", ".pth", ".ckpt", ".tflite" },
    };

    /// <summary>Registry of magic-byte signatures → modality. Inspected when
    /// extension dispatch fails. Reads up to 16 bytes of <paramref name="content"/>.</summary>
    private static readonly (byte[] Magic, string Modality)[] MagicSignatures =
    {
        (new byte[] { 0x89, 0x50, 0x4E, 0x47 },                              "image"),     // PNG
        (new byte[] { 0xFF, 0xD8, 0xFF },                                    "image"),     // JPEG
        (new byte[] { 0x47, 0x49, 0x46, 0x38 },                              "image"),     // GIF
        (new byte[] { 0x52, 0x49, 0x46, 0x46 },                              "audio"),     // RIFF (WAV)
        (new byte[] { 0x66, 0x4C, 0x61, 0x43 },                              "audio"),     // FLAC
        (new byte[] { 0x49, 0x44, 0x33 },                                    "audio"),     // ID3 / MP3
        (new byte[] { 0x4F, 0x67, 0x67, 0x53 },                              "audio"),     // OGG
        (new byte[] { 0x1A, 0x45, 0xDF, 0xA3 },                              "video"),     // MKV / WebM
        (new byte[] { 0x50, 0x4B, 0x03, 0x04 },                              "compressed"),// ZIP
        (new byte[] { 0x1F, 0x8B },                                          "compressed"),// gzip
        (new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },                  "compressed"),// 7z
    };

    public string Route(string artifactPath, Stream content)
    {
        ArgumentNullException.ThrowIfNull(artifactPath);
        ArgumentNullException.ThrowIfNull(content);

        var ext = Path.GetExtension(artifactPath);
        if (!string.IsNullOrEmpty(ext))
        {
            var lower = ext.ToLowerInvariant();
            foreach (var kv in ExtensionMap)
            {
                foreach (var registered in kv.Value)
                {
                    if (string.Equals(registered, lower, StringComparison.Ordinal))
                    {
                        return kv.Key;
                    }
                }
            }
        }

        // Magic-byte sniff (read up to 16 bytes; rewind if seekable).
        if (content.CanRead)
        {
            Span<byte> sniff = stackalloc byte[16];
            var startPos = content.CanSeek ? content.Position : 0L;
            var read = content.Read(sniff);
            if (content.CanSeek) { content.Position = startPos; }
            if (read > 0)
            {
                foreach (var (magic, modality) in MagicSignatures)
                {
                    if (read >= magic.Length && sniff[..magic.Length].SequenceEqual(magic))
                    {
                        return modality;
                    }
                }
            }
        }

        return UnknownModality;
    }
}
