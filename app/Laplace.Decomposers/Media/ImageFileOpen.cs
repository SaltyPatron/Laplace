using System.Diagnostics;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Opens on-disk image packaging (JPEG/PNG/BMP/GIF/TGA, natively decoded and sniffed) into
/// a planar RGBA recovery buffer. Neither container bytes nor RGBA bytes are identity inputs:
/// identity is the image composition ladder over codepoint-floor atoms.
/// </summary>
public static class ImageFileOpen
{
    public static Task<(uint Width, uint Height, byte[] Rgba)?> OpenAsync(
        string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (path.EndsWith(".rgba", StringComparison.OrdinalIgnoreCase))
            return RgbaFileCodec.OpenAsync(path, ct);

        var decoded = MediaDecode.DecodeImageFile(path);
        if (decoded is null)
        {
            Trace.TraceWarning("ImageFileOpen: native decode failed for '{0}'", path);
            return Task.FromResult<(uint, uint, byte[])?>(null);
        }
        return Task.FromResult<(uint, uint, byte[])?>(decoded);
    }

    public static bool IsSupportedPath(string path) =>
        path.EndsWith(".rgba", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase);
}
