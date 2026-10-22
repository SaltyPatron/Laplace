using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[Collection("Perfcache")]
public sealed unsafe class NativeScalarFloorParityTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Map([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CopyRecord(nint snapshot, nuint index, void* output);

    public NativeScalarFloorParityTests(PerfcacheTestFixture _) { }

    [Fact]
    public void EntireMappingMatchesTheIndependentNativeLibraryByteForByte()
    {
        var library = NativeLibrary.Load(Environment.GetEnvironmentVariable("LAPLACE_NATIVE_TEST_LIBRARY") ?? "laplace");
        try
        {
            var map = Marshal.GetDelegateForFunctionPointer<Map>(NativeLibrary.GetExport(library, "lp_tier0_map"));
            nint native = map(CodepointPerfcache.ResolveDefaultPath());
            Assert.NotEqual(nint.Zero, native);
            Assert.Equal(1_112_064, CodepointPerfcache.Count);
            var records = CodepointPerfcache.Records;
            var expected = new ReadOnlySpan<byte>((void*)native, checked(records.Length * 64));
            Assert.True(expected.SequenceEqual(MemoryMarshal.AsBytes(records)),
                "Every ID, fixed coordinate, Native Hilbert key and collation rank must match.");
            for (int i = 1; i < records.Length; i++)
                Assert.True(records[i - 1].Hash.CompareToBytewise(records[i].Hash) < 0);
            foreach (uint cp in new uint[] { 0, 0x41, 0xd7ff, 0xe000, 0x10ffff })
            {
                Assert.True(CodepointPerfcache.TryLookupScalar(cp, out var record));
                Assert.Equal(cp, record.Codepoint);
                Assert.True(CodepointPerfcache.TryLookupCodepoint(record.Hash, out uint reverse));
                Assert.Equal(cp, reverse);
            }
            Assert.False(CodepointPerfcache.TryLookupScalar(0xd800, out _));
            Assert.False(CodepointPerfcache.TryLookupScalar(0xdfff, out _));
            Assert.False(CodepointPerfcache.TryLookupScalar(0x110000, out _));
        }
        finally { NativeLibrary.Free(library); }
    }

    [Fact]
    public void CorruptSelectionCannotReplaceTheLiveMapping()
    {
        string path = Path.Combine(Path.GetTempPath(), "bad-native-floor-" + Guid.NewGuid().ToString("N"));
        var before = CodepointPerfcache.Receipt;
        var atom = CodepointPerfcache.LookupScalar(0x10ffff);
        try
        {
            File.WriteAllBytes(path, "LPRF-old"u8.ToArray());
            Assert.Throws<InvalidOperationException>(() => CodepointPerfcache.Load(path));
            Assert.Equal(before, CodepointPerfcache.Receipt);
            Assert.Equal(atom.Hash, CodepointPerfcache.LookupScalar(0x10ffff).Hash);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnicodeSnapshotCopyUsesTheNativeRecordSizeAndHasNoSeedingExport()
    {
        string directory = Path.Combine(Path.GetTempPath(), "native-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string xml = Path.Combine(directory, "ucd.xml"), ducet = Path.Combine(directory, "allkeys.txt");
        File.WriteAllText(xml, "<ucd><repertoire><char cp=\"0041\"/></repertoire></ucd>");
        File.WriteAllText(ducet, "0041 ; [.1.2.3]\n");
        string file = OperatingSystem.IsWindows() ? "laplace_core.dll" : "liblaplace_core.so";
        nint library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, file));
        try
        {
            using var snapshot = UnicodeSeed.OpenSnapshot(xml, ducet);
            Assert.Equal(1_112_064, snapshot.Count);
            Assert.Equal(CodepointPerfcache.Records[0].Hash, snapshot.RecordAt(0).Hash);
            var copy = Marshal.GetDelegateForFunctionPointer<CopyRecord>(
                NativeLibrary.GetExport(library, "laplace_unicode_seed_snapshot_copy_record"));
            Span<byte> guarded = stackalloc byte[80]; guarded.Fill(0xa5);
            fixed (byte* output = guarded) Assert.Equal(0, copy(snapshot.DangerousGetHandle(), 0, output));
            Assert.Equal(CodepointPerfcache.Records[0].Hash, MemoryMarshal.Read<CodepointRecord>(guarded).Hash);
            Assert.All(guarded[64..].ToArray(), value => Assert.Equal((byte)0xa5, value));
            Assert.False(NativeLibrary.TryGetExport(library, "laplace_unicode_seed_snapshot_stage", out _));
        }
        finally { NativeLibrary.Free(library); Directory.Delete(directory, recursive: true); }
    }
}
