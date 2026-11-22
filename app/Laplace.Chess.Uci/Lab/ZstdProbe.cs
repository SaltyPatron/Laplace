using System.Text.Json.Nodes;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Laplace.Chess.Uci.Lab;

/// <summary>
/// Exercises managed Zstandard streaming via <c>ZstdSharp</c>: one decompressor context with the configured
/// window limit decodes an exact checksummed two-game PGN fixture, and reports the loaded runtime library and SHA-256.
/// </summary>
public static class ZstdProbe
{
    private static readonly byte[] Frame = Convert.FromHexString(
        "28b52ffd0458950200d2c4111980b939e8ffff81055e128980723abab4cdcceebd9136e8767acb5d3aca47a131329ca811355277f974d4bd4cd11480975f378230600b2fafdcba0783002fbf4d03f72674c1600200805398a70e661a3a1174");
    private const string Game = "[Event \"Test\"]\n[White \"A\"]\n[Black \"B\"]\n[Result \"1-0\"]\n\n1. e4 e5 2. Nf3 1-0\n";
    private static readonly byte[] Expected = System.Text.Encoding.ASCII.GetBytes(Game + "\n" + Game.Replace("\"A\"", "\"C\""));

    public static JsonObject Probe(string? path = null, int windowLogMax = 27, string? expectedVersion = null)
    {
        if (windowLogMax < 10 || windowLogMax > (IntPtr.Size == 8 ? 31 : 30))
            throw new ArgumentException("LAPLACE_ZSTD_WINDOW_LOG_MAX must be 10 through 31 (30 on 32-bit hosts)");
        if (path is not null && !Path.IsPathRooted(path))
            throw new ArgumentException("LAPLACE_ZSTD_LIBRARY must be an absolute shared-library path");

        using var decompressor = new Decompressor();
        decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, windowLogMax);

        byte[] decoded;
        using (var source = new MemoryStream(Frame, writable: false))
        using (var stream = new DecompressionStream(source, decompressor))
        using (var destination = new MemoryStream(Expected.Length))
        {
            stream.CopyTo(destination);
            decoded = destination.ToArray();
        }

        if (!decoded.AsSpan().SequenceEqual(Expected))
            throw new InvalidOperationException("Zstandard probe did not recover the complete checksummed PGN fixture");

        string version = Methods.ZSTD_versionString();
        if (expectedVersion is not null && version != expectedVersion)
            throw new InvalidOperationException($"Zstandard {version} loaded; the verified release is {expectedVersion}");

        string? actual = path is not null && File.Exists(path)
            ? path
            : typeof(Decompressor).Assembly.Location is { Length: > 0 } loc && File.Exists(loc) ? loc : null;
        string requested = path ?? Path.GetFileName(actual) ?? "ZstdSharp";
        string? digest = null;
        try { if (actual is not null) digest = LabFiles.Sha256(actual); }
        catch (IOException) { actual = null; }

        return new JsonObject
        {
            ["library"] = actual,
            ["requested_library"] = requested,
            ["library_sha256"] = digest,
            ["version"] = version,
            ["window_log_max"] = windowLogMax,
            ["window_limit_bytes"] = 1L << windowLogMax,
            ["stream_buffer_bytes"] = 2 * 128 * 1024,
            ["fixture_sha256"] = LabFiles.Sha256(Frame),
            ["decoded_bytes"] = decoded.Length,
            ["verification"] = "managed ZstdSharp streaming decoder and exact checksummed PGN fixture; corpus ingestion is checked separately",
        };
    }
}
