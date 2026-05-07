namespace Laplace.Core;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over the native centroid mantissa codec. Phase 2 / Track D / D2.
/// </summary>
public sealed class CentroidCodec : ICentroidCodec
{
    public Point4D Encode(Point4D position, CentroidPayloadV1 payload)
    {
        var native = ToNative(position);
        var p = new NativeCentroidAbi.PayloadV1
        {
            PrimeFlags      = payload.PrimeFlags,
            EntityId        = payload.EntityId,
            StructuralFlags = payload.StructuralFlags,
            LanguageId      = payload.LanguageId,
            ModelId         = payload.ModelId,
            Tier            = payload.Tier,
            Reserved        = payload.Reserved,
        };
        NativeCentroidAbi.EncodeV1(ref native, in p);
        return ToManaged(native);
    }

    public CentroidPayloadV1 Decode(Point4D position)
    {
        var native = ToNative(position);
        NativeCentroidAbi.DecodeV1(in native, out var p);
        return new CentroidPayloadV1(
            PrimeFlags:      p.PrimeFlags,
            EntityId:        p.EntityId,
            StructuralFlags: p.StructuralFlags,
            LanguageId:      p.LanguageId,
            ModelId:         p.ModelId,
            Tier:            p.Tier,
            Reserved:        p.Reserved);
    }

    public Point4D StripPayload(Point4D position)
    {
        var native = ToNative(position);
        NativeCentroidAbi.StripPayloadV1(in native, out var stripped);
        return ToManaged(stripped);
    }

    private static NativeS3.Point4D ToNative(Point4D p) => new()
    {
        X = p.X, Y = p.Y, Z = p.Z, W = p.W,
    };

    private static Point4D ToManaged(NativeS3.Point4D p) => new(p.X, p.Y, p.Z, p.W);
}
