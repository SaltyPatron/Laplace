using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Laplace.Chess.Uci.Lab;

/// <summary>
/// Exercises the native Zstandard streaming ABI chess corpus ingestion uses: one decoder context with the configured
/// window limit decodes an exact checksummed two-game PGN fixture, and the loaded library is named by path and SHA-256.
/// </summary>
public static unsafe class ZstdProbe
{
    private static readonly byte[] Frame = Convert.FromHexString(
        "28b52ffd0458950200d2c4111980b939e8ffff81055e128980723abab4cdcceebd9136e8767acb5d3aca47a131329ca811355277f974d4bd4cd11480975f378230600b2fafdcba0783002fbf4d03f72674c1600200805398a70e661a3a1174");
    private const string Game = "[Event \"Test\"]\n[White \"A\"]\n[Black \"B\"]\n[Result \"1-0\"]\n\n1. e4 e5 2. Nf3 1-0\n";
    private static readonly byte[] Expected = System.Text.Encoding.ASCII.GetBytes(Game + "\n" + Game.Replace("\"A\"", "\"C\""));

    [StructLayout(LayoutKind.Sequential)]
    private struct Buffer { public nint Data; public nuint Size; public nuint Pos; }

    public static JsonObject Probe(string? path = null, int windowLogMax = 27, string? expectedVersion = null)
    {
        if (windowLogMax < 10 || windowLogMax > (IntPtr.Size == 8 ? 31 : 30))
            throw new ArgumentException("LAPLACE_ZSTD_WINDOW_LOG_MAX must be 10 through 31 (30 on 32-bit hosts)");
        if (path is not null && !Path.IsPathRooted(path)) throw new ArgumentException("LAPLACE_ZSTD_LIBRARY must be an absolute shared-library path");
        string[] names = path is not null ? [path]
            : OperatingSystem.IsWindows() ? ["libzstd.dll", "zstd.dll"]
            : OperatingSystem.IsMacOS() ? ["libzstd.1.dylib", "libzstd.dylib"] : ["libzstd.so.1", "libzstd.so"];
        nint library = 0;
        string requested = "";
        foreach (var name in names)
            if (NativeLibrary.TryLoad(name, out library)) { requested = name; break; }
        if (library == 0)
            throw new InvalidOperationException("Native Zstandard runtime unavailable; install libzstd or set LAPLACE_ZSTD_LIBRARY. Tried: " + string.Join(", ", names));
        nint Export(string name) => NativeLibrary.TryGetExport(library, name, out var f) ? f
            : throw new InvalidOperationException("Native Zstandard library lacks the required streaming ABI");
        var create = (delegate* unmanaged<nint>)Export("ZSTD_createDCtx");
        var free = (delegate* unmanaged<nint, nuint>)Export("ZSTD_freeDCtx");
        var setParameter = (delegate* unmanaged<nint, int, int, nuint>)Export("ZSTD_DCtx_setParameter");
        var decompress = (delegate* unmanaged<nint, Buffer*, Buffer*, nuint>)Export("ZSTD_decompressStream");
        var isError = (delegate* unmanaged<nuint, uint>)Export("ZSTD_isError");
        var errorName = (delegate* unmanaged<nuint, nint>)Export("ZSTD_getErrorName");
        var versionString = (delegate* unmanaged<nint>)Export("ZSTD_versionString");
        nint versionNumber = Export("ZSTD_versionNumber");

        nuint Check(nuint result)
        {
            if (isError(result) != 0) throw new InvalidOperationException("Native Zstandard probe failed: " + Marshal.PtrToStringAnsi(errorName(result)));
            return result;
        }

        nint context = create();
        if (context == 0) throw new InvalidOperationException("Native Zstandard could not allocate a decoder context");
        try
        {
            Check(setParameter(context, 100, windowLogMax));
            var destination = new byte[Expected.Length];
            fixed (byte* src = Frame)
            fixed (byte* dst = destination)
            {
                var incoming = new Buffer { Data = (nint)src, Size = (nuint)Frame.Length };
                var outgoing = new Buffer { Data = (nint)dst, Size = (nuint)destination.Length };
                nuint remaining = Check(decompress(context, &outgoing, &incoming));
                if (remaining != 0 || incoming.Pos != (nuint)Frame.Length || outgoing.Pos != (nuint)Expected.Length || !destination.AsSpan().SequenceEqual(Expected))
                    throw new InvalidOperationException("Native Zstandard probe did not recover the complete checksummed PGN fixture");
            }
        }
        finally { free(context); }
        string version = Marshal.PtrToStringAnsi(versionString()) ?? "";
        if (expectedVersion is not null && version != expectedVersion)
            throw new InvalidOperationException($"Native Zstandard {version} loaded; the verified release is {expectedVersion}");
        string? actual = LoadedPath(library, versionNumber, requested);
        string? digest = null;
        try { if (actual is not null) digest = LabFiles.Sha256(actual); }
        catch (IOException) { actual = null; }
        return new JsonObject
        {
            ["library"] = actual, ["requested_library"] = requested, ["library_sha256"] = digest, ["version"] = version,
            ["window_log_max"] = windowLogMax, ["window_limit_bytes"] = 1L << windowLogMax, ["stream_buffer_bytes"] = 2 * 128 * 1024,
            ["fixture_sha256"] = LabFiles.Sha256(Frame), ["decoded_bytes"] = Expected.Length,
            ["verification"] = "native streaming ABI and exact checksummed PGN fixture; corpus ingestion is checked separately",
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint module, char[] name, uint size);

    private static string? LoadedPath(nint library, nint symbol, string requested)
    {
        if (OperatingSystem.IsWindows())
        {
            var buffer = new char[32768];
            uint n = GetModuleFileNameW(library, buffer, (uint)buffer.Length);
            if (n > 0) return new string(buffer, 0, (int)n);
        }
        else if (OperatingSystem.IsLinux())
        {
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                var range = fields[0].Split('-');
                long low = Convert.ToInt64(range[0], 16), high = Convert.ToInt64(range[1], 16);
                if (low <= symbol && symbol < high && fields.Length == 6 && fields[^1].StartsWith('/')) return fields[^1].Trim();
            }
        }
        return Path.IsPathRooted(requested) && File.Exists(requested) ? requested : null;
    }
}
