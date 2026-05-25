using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Computes a physicality's content-addressed id per DESIGN.md (physicalities
/// table, <c>id</c> column): <c>BLAKE3-128</c> of the canonical byte image of
/// <c>(entity_id, source_id, kind, coord, trajectory)</c>. Centralised here so
/// every decomposer derives it identically (ADR 0016 — no per-source
/// reinvention).
///
/// <para>
/// Canonical byte layout (little-endian, the substrate wire order):
/// <code>
///   entity_id   16 bytes
///   source_id   16 bytes
///   kind         2 bytes   (smallint, matches physicalities.kind)
///   coord       32 bytes   (x, y, z, m as 4 × f64)
///   trajectory   N bytes   (4 × f64 per vertex, in order; empty for T0 atoms)
/// </code>
/// Two physicalities with the same entity/source/kind/coord/trajectory hash
/// identically and converge via <c>ON CONFLICT DO NOTHING</c> (RULES R5).
/// </para>
/// </summary>
public static class PhysicalityId
{
    public static Hash128 Compute(
        Hash128 entityId,
        Hash128 sourceId,
        PhysicalityKind kind,
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
