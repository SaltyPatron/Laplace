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
    // Mirrors laplace_attestation_staged_t exactly — field order IS the ABI.
    // Added alongside OpponentRdFp1e9 because the C struct places it there; any
    // other position silently shifts every field after it.
    public long OpponentRatingFp1e9;
    public long SumScoreFp1e9;
    public byte ObjectIsNull;
    public byte ContextIsNull;
    public byte IsAggregated;
    public byte FoldReplayable;
}
