namespace Laplace.SubstrateCRUD;

public sealed record ApplyResult(
    int EntitiesAttempted,
    int EntitiesInserted,
    int PhysicalitiesAttempted,
    int PhysicalitiesInserted,
    int AttestationsAttempted,
    int AttestationsInserted,
    int RoundTrips,
    TimeSpan WallClock,
    bool TrunkShortcircuitHit);
