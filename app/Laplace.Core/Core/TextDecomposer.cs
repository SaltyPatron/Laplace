using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Laplace.Native;

namespace Laplace.Engine.Core;

public static unsafe class TextDecomposer
{
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    public readonly record struct WordSegment(Hash128 Id, int Offset, int Length);

    private sealed class Definition(lp_ref[] children, uint bytes)
    {
        public readonly lp_ref[] Children = children;
        public readonly uint Bytes = bytes;
    }

    private struct Occurrence
    {
        public lp_ref Value;
        public int First;
        public int Count;
        public uint Offset;
        public uint Bytes;
        public uint Atom;
    }

    private sealed class RecordingState
    {
        public required lp_tier0_record* T0;
        public readonly Dictionary<Hash128, Definition> Definitions = [];
        public Exception? Error;
    }

    public static TierTree Run(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return TierTree.New(0);

        void* ctx = NativeTextIdentity.ThreadContext();
        lp_tier0_record* t0 = CodepointPerfcache.TablePointer;
        ValidateCanonicalUtf8(utf8);

        var recording = new RecordingState { T0 = t0 };
        var gc = GCHandle.Alloc(recording);
        lp_ref root;
        try
        {
            fixed (byte* input = utf8)
            {
                root = LaplaceNative.lp_text_decompose(
                    ctx, input, (nuint)utf8.Length, &RecordComposition, (void*)GCHandle.ToIntPtr(gc));
            }
            if (recording.Error is not null)
                throw new InvalidOperationException("Native text decomposition failed.", recording.Error);
        }
        finally
        {
            gc.Free();
        }

        var nodes = new List<Occurrence>
        {
            new() { Value = root, Offset = 0, Bytes = checked((uint)utf8.Length) },
        };

        for (int i = 0; i < nodes.Count; i++)
        {
            Occurrence parent = nodes[i];
            var parentId = Hash128.FromNative(parent.Value.id);
            if (!recording.Definitions.TryGetValue(parentId, out var def))
            {
                ContentBytes(recording, in parent.Value, out uint atom);
                var leaf = nodes[i];
                leaf.Atom = atom;
                nodes[i] = leaf;
                continue;
            }

            int first = nodes.Count;
            var updatedParent = nodes[i];
            updatedParent.First = first;
            updatedParent.Count = def.Children.Length;
            nodes[i] = updatedParent;

            uint offset = parent.Offset;
            for (int c = 0; c < def.Children.Length; c++)
            {
                lp_ref childVal = def.Children[c];
                uint childBytes = ContentBytes(recording, in childVal, out _);
                nodes.Add(new Occurrence
                {
                    Value = childVal,
                    Offset = offset,
                    Bytes = childBytes,
                });
                offset += childBytes;
            }

            if (offset != parent.Offset + parent.Bytes)
                throw new InvalidOperationException("Native text decomposition byte span mismatch.");
        }

        var previous = new List<int>(nodes.Count);
        var current = new List<int>(nodes.Count);
        var stack = new List<int>(nodes.Count);
        var tree = TierTree.New(nodes.Count);
        uint previousBase = 0;

        for (int level = 0; level <= root.tier; level++)
        {
            Frontier(nodes, level, current, stack);
            int child = 0;
            uint baseIdx = (uint)tree.NodeCount;
            for (int i = 0; i < current.Count; i++)
            {
                Occurrence node = nodes[current[i]];
                uint index;
                if (level == 0)
                {
                    index = tree.AddLeaf(0, node.Atom, node.Offset, node.Bytes);
                }
                else
                {
                    int first = child;
                    uint end = node.Offset + node.Bytes;
                    while (child < previous.Count && nodes[previous[child]].Offset < end)
                        child++;
                    if (first == child || nodes[previous[first]].Offset != node.Offset)
                        throw new InvalidOperationException("Native text frontier child span mismatch.");
                    index = tree.AddNode(
                        (byte)level,
                        previousBase + (uint)first,
                        (uint)(child - first),
                        node.Offset,
                        node.Bytes);
                }

                double[] coord =
                [
                    Math.ScaleB(node.Value.c.m[0], -53),
                    Math.ScaleB(node.Value.c.m[1], -53),
                    Math.ScaleB(node.Value.c.m[2], -53),
                    Math.ScaleB(node.Value.c.m[3], -53),
                ];
                tree.SetId(index, Hash128.FromNative(node.Value.id));
                tree.SetCoord(index, coord);
                tree.SetHilbert(index, Hilbert128.Encode(coord));
            }

            previousBase = baseIdx;
            (previous, current) = (current, previous);
        }

        tree.FinalizeParents();
        return tree;
    }

    public static TierTree Run(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Run(Utf8.GetBytes(text));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static lp_ref RecordComposition(void* sink, lp_ref* children, uint count, byte tier)
    {
        lp_ref value = LaplaceNative.lp_ref_compose(children, count, tier);
        var state = (RecordingState)GCHandle.FromIntPtr((nint)sink).Target!;
        if (state.Error is not null)
            return value;

        try
        {
            var id = Hash128.FromNative(value.id);
            if (state.Definitions.ContainsKey(id))
                return value;

            var childCopy = new lp_ref[count];
            uint totalBytes = 0;
            for (int i = 0; i < (int)count; i++)
            {
                childCopy[i] = children[i];
                totalBytes = checked(totalBytes + ContentBytes(state, in childCopy[i], out _));
            }

            state.Definitions[id] = new Definition(childCopy, totalBytes);
        }
        catch (Exception ex)
        {
            state.Error = ex;
        }

        return value;
    }

    private static uint ContentBytes(RecordingState state, in lp_ref value, out uint atom)
    {
        var id = Hash128.FromNative(value.id);
        if (state.Definitions.TryGetValue(id, out var def))
        {
            atom = 0;
            return def.Bytes;
        }

        lp_id nid = value.id;
        long cp = LaplaceNative.lp_tier0_codepoint(state.T0, &nid);
        if (cp < 0)
            throw new InvalidOperationException("Unrecognized Tier-0 scalar ID during text decomposition.");

        atom = (uint)cp;
        return (uint)new Rune((int)cp).Utf8SequenceLength;
    }

    private static void Frontier(List<Occurrence> nodes, int level, List<int> cut, List<int> stack)
    {
        cut.Clear();
        stack.Clear();
        stack.Add(0);
        while (stack.Count > 0)
        {
            int index = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var node = nodes[index];
            if (node.Value.tier <= level)
            {
                cut.Add(index);
            }
            else
            {
                for (int i = node.Count - 1; i >= 0; i--)
                    stack.Add(node.First + i);
            }
        }
    }

    private static void ValidateCanonicalUtf8(ReadOnlySpan<byte> utf8)
    {
        int i = 0;
        while (i < utf8.Length)
        {
            var status = Rune.DecodeFromUtf8(utf8[i..], out var rune, out int consumed);
            if (status != OperationStatus.Done || consumed <= 0 || consumed != rune.Utf8SequenceLength)
                throw new InvalidOperationException("Native text decomposition failed: invalid UTF-8 scalar content");
            i += consumed;
        }
    }

    private sealed class SegmentCollector(nint source, int length)
    {
        public readonly nint Source = source;
        public readonly int Length = length;
        public readonly List<WordSegment> Segments = [];
        public Exception? Error;
    }

    /// <summary>Native UAX word segments, including punctuation and excluding whitespace; tier does not select them.</summary>
    public static WordSegment[] WordSegments(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return [];
        ValidateCanonicalUtf8(utf8);
        void* ctx = NativeTextIdentity.ThreadContext();
        fixed (byte* input = utf8)
        {
            var collector = new SegmentCollector((nint)input, utf8.Length);
            var gc = GCHandle.Alloc(collector);
            try
            {
                LaplaceNative.lp_text_segments(ctx, input, (nuint)utf8.Length, &CollectSegment, (void*)GCHandle.ToIntPtr(gc));
                if (collector.Error is not null)
                    throw new InvalidOperationException("Native text segmentation failed.", collector.Error);
                return collector.Segments.ToArray();
            }
            finally
            {
                gc.Free();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CollectSegment(void* context, nuint offset, nuint length, lp_ref* value)
    {
        var collector = (SegmentCollector)GCHandle.FromIntPtr((nint)context).Target!;
        if (collector.Error is not null) return;
        try
        {
            int off = checked((int)offset);
            int len = checked((int)length);
            if (off < 0 || len < 0 || off + len > collector.Length)
                throw new InvalidOperationException("Native word segment exceeds input span.");

            var span = new ReadOnlySpan<byte>((byte*)collector.Source + off, len);
            if (!IsAllWhitespace(span))
                collector.Segments.Add(new WordSegment(Hash128.FromNative(value->id), off, len));
        }
        catch (Exception ex) { collector.Error = ex; }
    }

    private static bool IsAllWhitespace(ReadOnlySpan<byte> utf8)
    {
        int i = 0;
        while (i < utf8.Length)
        {
            if (Rune.DecodeFromUtf8(utf8[i..], out var rune, out int consumed) != OperationStatus.Done || consumed <= 0)
                return false;
            if (!Rune.IsWhiteSpace(rune))
                return false;
            i += consumed;
        }
        return true;
    }

    public static Hash128? ContentRootId(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return null;
        void* ctx = NativeTextIdentity.ThreadContext();
        lp_id id;
        fixed (byte* input = utf8)
        {
            if (!LaplaceNative.lp_text_id(ctx, input, (nuint)utf8.Length, &id))
                return null;
        }
        return Hash128.FromNative(id);
    }

    public static Hash128? ContentRootId(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? null : Hash128.Text(text);
    }

    public static Hash128? SourceRootId(ReadOnlySpan<byte> utf8) => ContentRootId(utf8);
}
