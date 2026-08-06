using System.Diagnostics;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Unpack on-disk audio packaging into mono PCM16 recovery. Native decode: WAV /
/// MP3 / FLAC / Ogg Vorbis (+ sniff). Recovery only — identity is the
/// codepoint-floor audio ladder, never blake3(pcm) as T0.
/// </summary>
public static class AudioFileOpen
{
    public static Task<(int SampleRate, short[] Samples)?> OpenAsync(
        string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var decoded = MediaDecode.DecodeAudioFile(path);
        if (decoded is null)
        {
            Trace.TraceWarning("AudioFileOpen: native decode failed for '{0}'", path);
            return Task.FromResult<(int, short[])?>(null);
        }
        return Task.FromResult<(int, short[])?>(decoded);
    }

    public static bool IsSupportedPath(string path) =>
        path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".wave", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".oga", StringComparison.OrdinalIgnoreCase);
}
