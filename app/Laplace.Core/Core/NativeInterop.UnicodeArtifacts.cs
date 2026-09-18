using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_validate_ucdxml", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnicodeSeedValidateUcdXml(string ucdxmlPath);

    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_snapshot_open",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnicodeSeedSnapshotOpen(
        string ucdxmlPath, string ducetPath, IntPtr* outSnapshot);

    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_snapshot_free")]
    internal static partial void UnicodeSeedSnapshotFree(IntPtr snapshot);

    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_snapshot_count")]
    internal static partial nuint UnicodeSeedSnapshotCount(IntPtr snapshot);

    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_snapshot_stage")]
    internal static partial int UnicodeSeedSnapshotStage(
        IntPtr snapshot, nuint first, nuint count, IntPtr stage, Hash128* sourceId);

    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_snapshot_copy_record")]
    internal static partial int UnicodeSeedSnapshotCopyRecord(
        IntPtr snapshot, nuint index, CodepointRecord* outRecord);
}
