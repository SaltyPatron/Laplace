using System.Diagnostics;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Unpacks on-disk audio packaging (WAV / MP3 / FLAC / Ogg Vorbis, natively decoded and
/// sniffed) into a mono PCM16 recovery buffer. The buffer is not an identity input: identity
/// is the audio composition ladder over codepoint-floor atoms.
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
