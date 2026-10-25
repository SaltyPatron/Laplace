using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static class TextDecomposer
{
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    public static unsafe TierTree Run(ReadOnlySpan<byte> utf8)
    {
        IntPtr handle = IntPtr.Zero;
        fixed (byte* input = utf8)
        {
            int status = NativeInterop.TextDecomposerRun(input, (nuint)utf8.Length, &handle);
            if (status != 0)
            {
                string reason = status switch
                {
                    -1 => "invalid arguments or unsupported size",
                    -2 => "invalid UTF-8 scalar content",
                    -3 => "canonical Native tier0.bin/ICU unavailable, or allocation failed",
                    _ => $"rc={status}",
                };
                throw new InvalidOperationException($"Native text decomposition failed: {reason}");
            }
        }
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Native text decomposition returned no tree.");
        return TierTree.FromExistingHandle(handle);
    }

    public static TierTree Run(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Run(Utf8.GetBytes(text));
    }

    public readonly record struct WordSegment(Hash128 Id, int Offset, int Length);

    private sealed class SegmentCollector(nint source)
    {
        public nint Source { get; } = source;
        public List<WordSegment> Segments { get; } = [];
        public Exception? Error { get; set; }
    }

    /// <summary>Native UAX word segments, including punctuation and excluding whitespace; tier does not select them.</summary>
    public static unsafe WordSegment[] WordSegments(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return [];
        fixed (byte* input = utf8)
        {
            var collector = new SegmentCollector((nint)input);
            var handle = GCHandle.Alloc(collector);
            try
            {
                int status = NativeInterop.ContentWordSegment(input, (nuint)utf8.Length,
                    &CollectSegment, (void*)GCHandle.ToIntPtr(handle));
                if (status != 0 || collector.Error is not null)
                    throw new InvalidOperationException($"Native text segmentation failed (rc={status}).", collector.Error);
                return collector.Segments.ToArray();
            }
            finally { handle.Free(); }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void CollectSegment(void* context, uint ordinal, byte* bytes, uint length, Hash128* id)
    {
        var collector = (SegmentCollector)GCHandle.FromIntPtr((nint)context).Target!;
        if (collector.Error is not null) return;
        try
        {
            if (ordinal != collector.Segments.Count)
                throw new InvalidOperationException("Native word segments are out of source order.");
            collector.Segments.Add(new WordSegment(*id, checked((int)((nint)bytes - collector.Source)), checked((int)length)));
        }
        catch (Exception error) { collector.Error = error; } // No managed exception may cross the Native callback.
    }

    public static unsafe Hash128? ContentRootId(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return null;
        Hash128 id;
        fixed (byte* input = utf8)
        {
            int status = NativeInterop.ContentRootId(input, (nuint)utf8.Length, &id);
            if (status == -1) return null;
            if (status != 0)
                throw new InvalidOperationException(
                    $"Native text identity failed (rc={status}); configure canonical LAPLACE_TIER0 and provision ICU.");
        }
        return id;
    }

    /// <summary>Source and ordinary text share the exact same Native scalar composition.</summary>
    public static Hash128? SourceRootId(ReadOnlySpan<byte> utf8) => ContentRootId(utf8);

    public static Hash128? ContentRootId(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? null : Hash128.Text(text);
    }
}
