using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Laplace.Engine.Core;

public enum XmlRecordFrameKind
{
    ContainerHeader,
    Record,
    Text,
}

public sealed record XmlRecordAttribute(
    string Name,
    string NamespaceUri,
    string Prefix,
    string Value);

public sealed record XmlRecordNode(
    string Name,
    string NamespaceUri,
    string Prefix,
    int Depth,
    IReadOnlyList<XmlRecordAttribute> Attributes,
    IReadOnlyList<XmlRecordNode> Children,
    string Value)
{
    public string Attribute(string name, string fallback = "") =>
        Attributes.FirstOrDefault(attribute =>
            attribute.Name.Equals(name, StringComparison.Ordinal))?.Value ?? fallback;

    public IEnumerable<XmlRecordNode> ChildrenNamed(string name) =>
        Children.Where(child => child.Name.Equals(name, StringComparison.Ordinal));
}

public readonly record struct XmlRecordFrame(
    XmlRecordFrameKind Kind,
    XmlRecordNode Node);

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeXmlEvent
{
    internal readonly int Kind;
    internal readonly int Depth;
    internal readonly IntPtr Name;
    internal readonly IntPtr Value;
    internal readonly nuint ValueLength;
    internal readonly IntPtr NamespaceUri;
    internal readonly IntPtr Prefix;
}

/// <summary>
/// Bounded native XML stream projected into shallow container headers and complete
/// record subtrees. The native SAX parser owns XML syntax, entity decoding and
/// network/DTD policy; callers only interpret source-specific element names.
/// </summary>
public static class XmlRecordReader
{
    public static async IAsyncEnumerable<XmlRecordFrame> ReadAsync(
        string filePath,
        int recordDepth,
        int bufferSize = 128 * 1024,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await foreach (XmlRecordFrame frame in ReadAsync(
            stream, recordDepth, bufferSize, observeBufferedCharacters: null, ct: ct)
            .ConfigureAwait(false))
            yield return frame;
    }

    internal static async IAsyncEnumerable<XmlRecordFrame> ReadAsync(
        Stream stream,
        int recordDepth,
        int bufferSize,
        Action<int>? observeBufferedCharacters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("stream must be readable", nameof(stream));
        if (recordDepth < 0) throw new ArgumentOutOfRangeException(nameof(recordDepth));
        if (bufferSize < 1) throw new ArgumentOutOfRangeException(nameof(bufferSize));

        using var parser = NativeXmlStream.New();
        var stack = new List<NodeBuilder>(16);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct)
                    .ConfigureAwait(false);
                bool final = read == 0;
                NativeXmlEvent[] events = parser.Feed(buffer.AsSpan(0, read), final);
                foreach (NativeXmlEvent item in events)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (XmlRecordFrame frame in Consume(item, stack, recordDepth))
                        yield return frame;
                    if (observeBufferedCharacters is not null)
                    {
                        int buffered = 0;
                        foreach (NodeBuilder open in stack)
                            buffered = checked(buffered + open.BufferedValueLength);
                        observeBufferedCharacters(buffered);
                    }
                }
                if (final) break;
            }
            if (stack.Count != 0)
                throw new InvalidDataException("native XML stream ended with unclosed elements");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IEnumerable<XmlRecordFrame> Consume(
        NativeXmlEvent item,
        List<NodeBuilder> stack,
        int recordDepth)
    {
        if (item.Kind != NativeXmlStream.Attribute)
        {
            foreach (NodeBuilder open in stack)
            {
                if (open.Depth >= recordDepth || open.HeaderEmitted) continue;
                open.HeaderEmitted = true;
                yield return new XmlRecordFrame(
                    XmlRecordFrameKind.ContainerHeader,
                    open.Build(includeChildren: false));
            }
        }

        switch (item.Kind)
        {
            case NativeXmlStream.Start:
            {
                if (item.Depth != stack.Count)
                    throw MalformedEvent(item, stack.Count);
                stack.Add(new NodeBuilder(
                    Name(item), PointerString(item.NamespaceUri), PointerString(item.Prefix), item.Depth));
                break;
            }
            case NativeXmlStream.Attribute:
            {
                if (stack.Count == 0 || stack[^1].Depth != item.Depth)
                    throw MalformedEvent(item, stack.Count);
                stack[^1].AddAttribute(new XmlRecordAttribute(
                    Name(item), PointerString(item.NamespaceUri), PointerString(item.Prefix), Value(item)));
                break;
            }
            case NativeXmlStream.Text:
            {
                if (stack.Count == 0 || stack[^1].Depth != item.Depth)
                    throw MalformedEvent(item, stack.Count);
                string value = Value(item);
                if (item.Depth < recordDepth)
                {
                    NodeBuilder parent = stack[^1];
                    yield return new XmlRecordFrame(
                        XmlRecordFrameKind.Text,
                        new XmlRecordNode(
                            parent.Name,
                            parent.NamespaceUri,
                            parent.Prefix,
                            parent.Depth,
                            [],
                            [],
                            value));
                }
                else
                {
                    foreach (NodeBuilder open in stack)
                        if (open.Depth >= recordDepth) open.AppendValue(value);
                }
                break;
            }
            case NativeXmlStream.End:
            {
                if (stack.Count == 0 || stack[^1].Depth != item.Depth
                    || !stack[^1].Name.Equals(Name(item), StringComparison.Ordinal)
                    || !stack[^1].NamespaceUri.Equals(PointerString(item.NamespaceUri), StringComparison.Ordinal)
                    || !stack[^1].Prefix.Equals(PointerString(item.Prefix), StringComparison.Ordinal))
                    throw MalformedEvent(item, stack.Count);
                NodeBuilder complete = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                XmlRecordNode node = complete.Build(includeChildren: true);
                if (complete.Depth == recordDepth)
                    yield return new XmlRecordFrame(XmlRecordFrameKind.Record, node);
                else if (complete.Depth > recordDepth && stack.Count > 0)
                    stack[^1].AddChild(node);
                break;
            }
            default:
                throw new InvalidDataException($"native XML stream returned unknown event kind {item.Kind}");
        }
    }

    private static InvalidDataException MalformedEvent(NativeXmlEvent item, int openDepth) =>
        new($"native XML stream returned inconsistent event kind={item.Kind} depth={item.Depth} open={openDepth}");

    private static string Name(NativeXmlEvent item) =>
        Marshal.PtrToStringUTF8(item.Name)
        ?? throw new InvalidDataException("native XML event has no element name");

    private static string PointerString(IntPtr value) =>
        value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;

    private static unsafe string Value(NativeXmlEvent item)
    {
        if (item.ValueLength == 0) return string.Empty;
        if (item.Value == IntPtr.Zero)
            throw new InvalidDataException("native XML event has a null nonempty value");
        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(
            item.Value.ToPointer(), checked((int)item.ValueLength)));
    }

    private sealed class NodeBuilder(
        string name,
        string namespaceUri,
        string prefix,
        int depth)
    {
        private readonly List<XmlRecordAttribute> _attributes = [];
        private readonly List<XmlRecordNode> _children = [];
        private readonly StringBuilder _value = new();

        internal string Name { get; } = name;
        internal string NamespaceUri { get; } = namespaceUri;
        internal string Prefix { get; } = prefix;
        internal int Depth { get; } = depth;
        internal bool HeaderEmitted { get; set; }
        internal int BufferedValueLength => _value.Length;

        internal void AddAttribute(XmlRecordAttribute attribute)
        {
            if (_attributes.Any(existing =>
                existing.Name.Equals(attribute.Name, StringComparison.Ordinal)
                && existing.NamespaceUri.Equals(attribute.NamespaceUri, StringComparison.Ordinal)))
                throw new InvalidDataException(
                    $"duplicate XML attribute '{attribute.Name}' on '{Name}'");
            _attributes.Add(attribute);
        }

        internal void AddChild(XmlRecordNode child) => _children.Add(child);
        internal void AppendValue(string value) => _value.Append(value);

        internal XmlRecordNode Build(bool includeChildren) => new(
            Name,
            NamespaceUri,
            Prefix,
            Depth,
            _attributes.ToArray(),
            includeChildren ? _children.ToArray() : [],
            _value.ToString());
    }

    private sealed class NativeXmlStream : SafeHandle
    {
        internal const int Start = 1;
        internal const int End = 2;
        internal const int Text = 3;
        internal const int Attribute = 4;

        private NativeXmlStream(IntPtr value) : base(IntPtr.Zero, ownsHandle: true) =>
            SetHandle(value);

        public override bool IsInvalid => handle == IntPtr.Zero;

        internal static unsafe NativeXmlStream New()
        {
            IntPtr value = IntPtr.Zero;
            int rc = NativeInterop.XmlStreamNew(&value);
            return rc switch
            {
                0 when value != IntPtr.Zero => new NativeXmlStream(value),
                -3 => throw new OutOfMemoryException("native XML stream allocation failed"),
                _ => throw new InvalidOperationException($"native XML stream creation failed ({rc})"),
            };
        }

        internal unsafe NativeXmlEvent[] Feed(ReadOnlySpan<byte> utf8, bool final)
        {
            ObjectDisposedException.ThrowIf(IsInvalid, this);
            NativeXmlEvent* events = null;
            nuint count = 0;
            int rc;
            fixed (byte* input = utf8)
                rc = NativeInterop.XmlStreamFeed(
                    handle, input, (nuint)utf8.Length, final ? 1 : 0, &events, &count);
            if (rc == -2)
                throw new InvalidDataException(
                    Marshal.PtrToStringUTF8(NativeInterop.XmlStreamError(handle))
                    ?? "malformed XML input");
            if (rc == -3) throw new OutOfMemoryException("native XML stream allocation failed");
            if (rc != 0) throw new InvalidOperationException($"native XML stream feed failed ({rc})");
            if (count == 0) return [];
            if (events is null)
                throw new InvalidDataException("native XML stream returned a null event batch");
            int length = checked((int)count);
            var result = new NativeXmlEvent[length];
            new ReadOnlySpan<NativeXmlEvent>(events, length).CopyTo(result);
            return result;
        }

        protected override bool ReleaseHandle()
        {
            NativeInterop.XmlStreamFree(handle);
            return true;
        }
    }
}
