using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public static class PhysicalityId
{
    public static Hash128 Compute(
        Hash128 entityId,
        Hash128 sourceId,
        PhysicalityType kind,
        double coordX, double coordY, double coordZ, double coordM,
        ReadOnlySpan<double> trajectory)
    {
        int trajBytes = trajectory.Length * sizeof(double);
        int total = 16 + 16 + 2 + 32 + trajBytes;
        byte[] buf = total <= 256 ? null! : new byte[total];
        Span<byte> span = buf is null ? stackalloc byte[total] : buf;

        int o = 0;
        entityId.WriteBytes(span.Slice(o, 16)); o += 16;
        sourceId.WriteBytes(span.Slice(o, 16)); o += 16;
        BitConverter.TryWriteBytes(span.Slice(o, 2), (short)kind); o += 2;
        BitConverter.TryWriteBytes(span.Slice(o, 8), coordX); o += 8;
        BitConverter.TryWriteBytes(span.Slice(o, 8), coordY); o += 8;
        BitConverter.TryWriteBytes(span.Slice(o, 8), coordZ); o += 8;
        BitConverter.TryWriteBytes(span.Slice(o, 8), coordM); o += 8;
        for (int i = 0; i < trajectory.Length; i++)
        {
            BitConverter.TryWriteBytes(span.Slice(o, 8), trajectory[i]); o += 8;
        }
        return Hash128.Blake3(span);
    }
}
