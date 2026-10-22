using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[Collection("Perfcache")]
public sealed unsafe class NativeHighwayParityTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Map([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Find(nint mapping, Hash128* id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Fingerprint(nint mapping, byte* output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint List(nint mapping, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint At(nint mapping, nint list, uint slot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint Edges(nint mapping, [MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        uint slot, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, out nint edges);

    public NativeHighwayParityTests(PerfcacheTestFixture _) { HighwayPerfcache.LoadDefault(); }

    [Fact]
    public void EntireMappingAndEightNamespacesMatchIndependentNativeLibrary()
    {
        nint library = NativeLibrary.Load(Environment.GetEnvironmentVariable("LAPLACE_NATIVE_TEST_LIBRARY") ?? "laplace");
        try
        {
            var map = Marshal.GetDelegateForFunctionPointer<Map>(NativeLibrary.GetExport(library, "lp_highway_map"));
            var find = Marshal.GetDelegateForFunctionPointer<Find>(NativeLibrary.GetExport(library, "lp_highway_find"));
            var list = Marshal.GetDelegateForFunctionPointer<List>(NativeLibrary.GetExport(library, "lp_highway_list"));
            var at = Marshal.GetDelegateForFunctionPointer<At>(NativeLibrary.GetExport(library, "lp_highway_at"));
            var fingerprint = Marshal.GetDelegateForFunctionPointer<Fingerprint>(NativeLibrary.GetExport(library, "lp_highway_fingerprint"));
            nint mapping = map(HighwayPerfcache.ResolveDefaultPath());
            Assert.NotEqual(nint.Zero, mapping);
            // lp_highway's public ABI starts with rec and nrec (pointer, size_t).
            nint records = Marshal.ReadIntPtr(mapping);
            nuint count = *(nuint*)((byte*)mapping + IntPtr.Size);
            Assert.Equal((nuint)HighwayPerfcache.Records.Length, count);
            Assert.True(count > 100_000, "Use the real linguistic highway, not a synthetic cache.");
            var expected = new ReadOnlySpan<byte>((void*)records, checked((int)count * 64));
            Assert.True(expected.SequenceEqual(MemoryMarshal.AsBytes(HighwayPerfcache.Records)));
            byte[] hash = new byte[32];
            fixed (byte* output = hash) fingerprint(mapping, output);
            Assert.Equal(hash, HighwayPerfcache.Fingerprint());

            foreach (string name in new[] { "ili", "Synset", "fnframe", "fnfe", "fnlu", "vnclass", "pbroleset", "vaframe" })
            {
                nint descriptor = list(mapping, name);
                Assert.NotEqual(nint.Zero, descriptor);
                // lp_list has name[32], say[64], first, count.
                uint slots = *(uint*)((byte*)descriptor + 100);
                nint record = 0;
                for (uint slot = 0; slot < slots && record == 0; slot++) record = at(mapping, descriptor, slot);
                Assert.NotEqual(nint.Zero, record);
                var native = *(HighwayRecord*)record;
                Assert.True(HighwayPerfcache.TryFind(native.Hash, out var actual), name);
                Hash128 id = native.Hash;
                nint independentlyFound = find(mapping, &id);
                Assert.NotEqual(nint.Zero, independentlyFound);
                Assert.True(new ReadOnlySpan<byte>((void*)independentlyFound, 64)
                    .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<HighwayRecord>(in actual))));
            }
        }
        finally { NativeLibrary.Free(library); }
    }

    [Fact]
    public void RealIliSynsetCrosswalkMatchesNativeEdges()
    {
        Assert.True(HighwayPerfcache.TryLookup("ili", "i46360", out var dog));
        uint[] actual = HighwayPerfcache.Crosswalk("ili", dog.Slot, "Synset");
        Assert.NotEmpty(actual);
        nint library = NativeLibrary.Load(Environment.GetEnvironmentVariable("LAPLACE_NATIVE_TEST_LIBRARY") ?? "laplace");
        try
        {
            var map = Marshal.GetDelegateForFunctionPointer<Map>(NativeLibrary.GetExport(library, "lp_highway_map"));
            var edges = Marshal.GetDelegateForFunctionPointer<Edges>(NativeLibrary.GetExport(library, "lp_highway_edges"));
            nint mapping = map(HighwayPerfcache.ResolveDefaultPath());
            nuint count = edges(mapping, "ili", dog.Slot, "Synset", out nint native);
            Assert.Equal((nuint)actual.Length, count);
            for (int i = 0; i < actual.Length; i++)
                Assert.Equal(*(uint*)((byte*)native + i * 8 + 4), actual[i]);
        }
        finally { NativeLibrary.Free(library); }
    }

    [Fact]
    public void RejectedCacheCannotReplacePublishedMapping()
    {
        byte[] before = HighwayPerfcache.Fingerprint();
        string path = Path.Combine(Path.GetTempPath(), $"laplace-invalid-highway-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, "LHWY retired private ROM"u8.ToArray());
            Assert.Throws<InvalidOperationException>(() => HighwayPerfcache.Load(path));
            Assert.True(HighwayPerfcache.IsLoaded);
            Assert.Equal(before, HighwayPerfcache.Fingerprint());
            Assert.True(HighwayPerfcache.TryLookup("upos", "NOUN", out var noun));
            Assert.Equal(Hash128.Text("NOUN"), noun.Hash);
        }
        finally { File.Delete(path); }
    }
}
