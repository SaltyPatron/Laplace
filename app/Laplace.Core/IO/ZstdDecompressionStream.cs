using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Laplace.Engine.Core.IO;

/// <summary>
/// Streaming transport for the native Zstandard decoder. Native code owns the
/// codec; managed code transfers bounded blocks and never materializes an archive.
/// Concatenated frames are supported, and incomplete frames fail at end of input.
/// </summary>
public sealed class ZstdDecompressionStream : Stream
{
    public const int DefaultWindowLogMax = 27; // 128 MiB native history window.
    private const int BlockSize = 128 * 1024;
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly Api _api;
    private readonly Context _context;
    private readonly byte[] _input = new byte[BlockSize];
    private readonly byte[] _output = new byte[BlockSize];
    private readonly object _nativeGate = new();
    private int _inputPosition, _inputLength, _outputPosition, _outputLength;
    private nuint _hint = 1;
    private bool _sourceEnded, _frameSeen, _ended, _disposed, _drainOutput;
    private InvalidDataException? _failure;

    public ZstdDecompressionStream(Stream source, string? libraryPath = null,
        int windowLogMax = DefaultWindowLogMax, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("A readable source is required.", nameof(source));
        if (windowLogMax is < 10 or > 31 || (IntPtr.Size == 4 && windowLogMax > 30))
            throw new ArgumentOutOfRangeException(nameof(windowLogMax), "Use a Zstandard window log from 10 to 31 (30 on 32-bit hosts).");
        _source = source;
        _leaveOpen = leaveOpen;
        _api = new Api(libraryPath);
        try
        {
            _context = new Context(_api);
            // Stable public ZSTD_dParameter enum value, ZSTD_d_windowLogMax.
            _api.Check(_api.SetParameter(_context.DangerousGetHandle(), 100, windowLogMax));
        }
        catch
        {
            _context?.Dispose();
            _api.Dispose();
            throw;
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null) throw new InvalidDataException("The Zstandard stream has failed and cannot be resumed.", _failure);
        if (buffer.IsEmpty) return 0;
        while (_outputPosition == _outputLength && !_ended)
        {
            if (_inputPosition == _inputLength && !_sourceEnded && !_drainOutput)
                AcceptInput(_source.Read(_input));
            DecodeBlock();
        }
        int count = Math.Min(buffer.Length, _outputLength - _outputPosition);
        _output.AsSpan(_outputPosition, count).CopyTo(buffer);
        _outputPosition += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null) throw new InvalidDataException("The Zstandard stream has failed and cannot be resumed.", _failure);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return 0;
        while (_outputPosition == _outputLength && !_ended)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_inputPosition == _inputLength && !_sourceEnded && !_drainOutput)
                AcceptInput(await _source.ReadAsync(_input.AsMemory(), cancellationToken).ConfigureAwait(false));
            DecodeBlock();
        }
        int count = Math.Min(buffer.Length, _outputLength - _outputPosition);
        _output.AsMemory(_outputPosition, count).CopyTo(buffer);
        _outputPosition += count;
        return count;
    }

    private void AcceptInput(int count)
    {
        _inputPosition = 0;
        _inputLength = count;
        _sourceEnded = count == 0;
    }

    private void DecodeBlock()
    {
        // A pending source read may finish after Dispose. No native call may use
        // a released context or unloaded function pointer in that sequence.
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { DecodeBlockCore(); }
            catch (InvalidDataException error) { _failure = error; throw; }
        }
    }

    private unsafe void DecodeBlockCore()
    {
        if (_sourceEnded && _inputPosition == _inputLength && _hint == 0 && _frameSeen)
        {
            _ended = true;
            return;
        }
        fixed (byte* input = _input)
        fixed (byte* output = _output)
        {
            var source = new NativeBuffer { Data = (nint)input, Size = (nuint)_inputLength, Position = (nuint)_inputPosition };
            var target = new NativeBuffer { Data = (nint)output, Size = (nuint)_output.Length };
            int previousPosition = _inputPosition;
            _hint = _api.Decode(_context.DangerousGetHandle(), ref target, ref source);
            _api.Check(_hint);
            _inputPosition = checked((int)source.Position);
            _outputPosition = 0;
            _outputLength = checked((int)target.Position);
            _drainOutput = _outputLength == _output.Length && _hint != 0;
            if (_hint == 0) _frameSeen = true;
            if (_sourceEnded && _outputLength == 0 && _hint != 0)
                throw new InvalidDataException("The Zstandard input is empty or ends inside an incomplete frame.");
            if (!_sourceEnded && _outputLength == 0 && _inputPosition == previousPosition && previousPosition < _inputLength)
                throw new InvalidDataException("The native Zstandard decoder made no progress on the supplied input.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_nativeGate)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    try { _context.Dispose(); }
                    finally
                    {
                        _api.Dispose();
                        if (!_leaveOpen) _source.Dispose();
                    }
                }
            }
        }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBuffer { public nint Data; public nuint Size; public nuint Position; }

    private sealed class Context : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly Api _api;
        public Context(Api api) : base(true)
        {
            _api = api;
            bool retained = false;
            api.DangerousAddRef(ref retained);
            try
            {
                SetHandle(api.Create());
                if (IsInvalid) throw new OutOfMemoryException("Zstandard could not allocate a decoder context.");
            }
            catch
            {
                if (retained) api.DangerousRelease();
                throw;
            }
        }
        protected override bool ReleaseHandle()
        {
            try { _api.Free(handle); return true; }
            finally { _api.DangerousRelease(); }
        }
    }

    private sealed class Api : SafeHandleZeroOrMinusOneIsInvalid
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint CreateContext();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nuint FreeContext(nint context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nuint Parameter(nint context, int parameter, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nuint Decompress(nint context, ref NativeBuffer output, ref NativeBuffer input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint IsError(nuint result);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ErrorName(nuint result);
        private readonly nint _library;
        internal readonly CreateContext Create;
        internal readonly FreeContext Free;
        internal readonly Parameter SetParameter;
        internal readonly Decompress Decode;
        private readonly IsError _isError;
        private readonly ErrorName _errorName;

        public Api(string? path) : base(true)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Path.IsPathFullyQualified(path))
                throw new ArgumentException("LAPLACE_ZSTD_LIBRARY must be an absolute shared-library path.", nameof(path));
            string[] names = !string.IsNullOrWhiteSpace(path) ? [path]
                : OperatingSystem.IsWindows() ? ["libzstd.dll", "zstd.dll"]
                : OperatingSystem.IsMacOS() ? ["libzstd.1.dylib", "libzstd.dylib"] : ["libzstd.so.1", "libzstd.so"];
            foreach (string name in names)
                if (NativeLibrary.TryLoad(name, typeof(ZstdDecompressionStream).Assembly, null, out _library)) break;
            if (_library == 0)
                throw new DllNotFoundException("Native Zstandard is required for .pgn.zst input. Install the libzstd runtime or set LAPLACE_ZSTD_LIBRARY to its absolute shared-library path. Tried: " + string.Join(", ", names));
            SetHandle(_library);
            try
            {
                Create = Bind<CreateContext>("ZSTD_createDCtx");
                Free = Bind<FreeContext>("ZSTD_freeDCtx");
                SetParameter = Bind<Parameter>("ZSTD_DCtx_setParameter");
                Decode = Bind<Decompress>("ZSTD_decompressStream");
                _isError = Bind<IsError>("ZSTD_isError");
                _errorName = Bind<ErrorName>("ZSTD_getErrorName");
            }
            catch { Dispose(); throw; }
        }

        private T Bind<T>(string name) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
        internal void Check(nuint result)
        {
            if (_isError(result) != 0)
                throw new InvalidDataException("Native Zstandard decompression failed: " + Marshal.PtrToStringUTF8(_errorName(result))
                    + ". LAPLACE_ZSTD_WINDOW_LOG_MAX controls the admitted decoder history window.");
        }
        protected override bool ReleaseHandle() { NativeLibrary.Free(handle); return true; }
    }
}
