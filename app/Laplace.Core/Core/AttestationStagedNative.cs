using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential)]
public struct AttestationStagedNative
{
    public Hash128 Id;
    public Hash128 SubjectId;
    public Hash128 TypeId;
    public Hash128 ObjectId;
    public Hash128 SourceId;
    public Hash128 ContextId;
    public short Outcome;
    public long LastObservedAtUnixUs;
    public long ObservationCount;
    public long ScoreFp1e9;
    public long OpponentRdFp1e9;
    // Mirrors laplace_attestation_staged_t exactly — field order is the ABI, so this
    // field must follow OpponentRdFp1e9 as it does in the C struct.
    public long OpponentRatingFp1e9;
    public long SumScoreFp1e9;
    public byte ObjectIsNull;
    public byte ContextIsNull;
    public byte IsAggregated;
    public byte FoldReplayable;
}
