using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Managed face of <c>laplace_media_decode_*</c> — packaging unpack only.
/// Returns planar RGBA / mono int16 recovery buffers. These buffers are source
/// recovery state, not semantic identity by themselves. The current downstream
/// codepoint/decimal media ladder is a legacy implementation owned for repair by
/// GH #1134; docs/invention/modality-ladder-law.md defines the current law.
/// </summary>
public static class MediaDecode
{
    public static (uint Width, uint Height, byte[] Rgba)? DecodeImageFile(string path)
    {
        int rc = NativeInterop.MediaDecodeImageFile(path, out var native);
        if (rc != 0 || native.Rgba == IntPtr.Zero || native.Width == 0 || native.Height == 0)
            return null;
        try
        {
            int nbytes = checked((int)(native.Width * native.Height * 4u));
            var rgba = new byte[nbytes];
            Marshal.Copy(native.Rgba, rgba, 0, nbytes);
            return (native.Width, native.Height, rgba);
        }
        finally
        {
            NativeInterop.MediaFree(native.Rgba);
        }
    }

    public static (int SampleRate, short[] Samples)? DecodeAudioFile(string path)
    {
        int rc = NativeInterop.MediaDecodeAudioFile(path, out var native);
        if (rc != 0 || native.Pcm == IntPtr.Zero || native.NSamples == 0)
            return null;
        try
        {
            int n = checked((int)native.NSamples);
            var samples = new short[n];
            Marshal.Copy(native.Pcm, samples, 0, n);
            return (native.SampleRate, samples);
        }
        finally
        {
            NativeInterop.MediaFree(native.Pcm);
        }
    }
}
