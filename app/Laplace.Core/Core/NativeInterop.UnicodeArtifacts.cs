using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_compute_ducet", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnicodeSeedComputeDucet(
        string ducetPath,
        CodepointRecord* outRecords,
        nuint outCapacity);

    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_validate_ucdxml", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnicodeSeedValidateUcdXml(string ucdxmlPath);
}
