namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

internal static partial class NativeCentroidAbi
{
    /// <summary>Native layout matching <c>laplace_centroid_payload_v1_t</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PayloadV1
    {
        public ulong  PrimeFlags;
        public uint   EntityId;
        public byte   Modality;
        public ushort LanguageId;
        public byte   ModelId;
        public byte   Tier;
        public uint   Reserved;
    }

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_centroid_encode_v1")]
    internal static partial void EncodeV1(ref NativeS3.Point4D position, in PayloadV1 payload);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_centroid_decode_v1")]
    internal static partial void DecodeV1(in NativeS3.Point4D position, out PayloadV1 payload);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_centroid_strip_payload_v1")]
    internal static partial void StripPayloadV1(in NativeS3.Point4D position, out NativeS3.Point4D outGeometry);
}
