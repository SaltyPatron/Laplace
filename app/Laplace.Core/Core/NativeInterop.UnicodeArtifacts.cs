using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "laplace_unicode_seed_validate_ucdxml", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnicodeSeedValidateUcdXml(string ucdxmlPath);
}
