namespace Laplace.Engine.Core;

/// <summary>
/// One cell of an aggregated arena batch — layout-matched to the engine's
/// laplace_attestation_aggregated_cell_t (hash128, hash128, u8 + pad, i64, i64).
/// </summary>
public struct AttestationAggregatedCellNative
{
    public Hash128 Subject;
    public Hash128 Object;
    public byte ObjectIsNull;
    public long Games;
    public long SumScoreFp1e9;
}
