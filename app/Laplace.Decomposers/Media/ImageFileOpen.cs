using System.Diagnostics;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Open on-disk image packaging into planar RGBA recovery. Native decode covers
/// JPEG/PNG/BMP/GIF/TGA (and sniff). Recovery buffer only — identity is the
/// codepoint-floor image ladder, never container bytes and never blake3(rgba) as T0.
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
