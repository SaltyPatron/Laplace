using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

internal sealed record WorkingSetReconciliation(Hash128 LegacyMarkerAttestationId);
