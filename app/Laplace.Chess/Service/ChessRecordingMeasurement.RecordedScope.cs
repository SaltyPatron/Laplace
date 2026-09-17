using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

internal sealed partial class ChessRecordingMeasurement
{
    internal async Task<ScopeRequest> ReadRetainedScopeBeforeAsync(NpgsqlDataSource ds,
        IReadOnlyList<EntityRow> sourceEntities, IReadOnlyList<AttestationRow> witnesses,
        IReadOnlyList<PhysicalityRow> carriers, CancellationToken ct)
    {
        if (!RequireNoWriterWork || _corpusEvidence is null)
            throw new InvalidOperationException("retained recording scope requires the explicit read-only verifier");
        var retained = await _corpusEvidence.ReadRetainedScopeAsync(ct);
        var entities = SelectRetainedRows(retained.EntityIds, sourceEntities, row => row.Id, "entity");
        var physicalities = SelectRetainedRows(retained.PhysicalityIds, carriers, row => row.Id, "physicality");
        var testimony = SelectRetainedRows(retained.WitnessIds, witnesses, row => row.Id, "witness");
        return await ReadScopeBeforeAsync(ds, entities, testimony, physicalities, ct);
    }

    // Existing evidence selects an exact subset of current canonical composition. Later
    // metadata can add rows, but no historical identity can disappear or change.
    internal static T[] SelectRetainedRows<T>(IReadOnlyList<string> retained,
        IReadOnlyList<T> current, Func<T, Hash128> identity, string kind)
    {
        if (retained.Any(id => !ChessRecordedSelection.Hex(id, 32))
            || retained.Distinct(StringComparer.Ordinal).Count() != retained.Count)
            throw new InvalidDataException("retained recording scope has invalid or duplicate " + kind + " IDs");
        var rows = current.DistinctBy(identity)
            .ToDictionary(row => Convert.ToHexStringLower(identity(row).ToBytes()), StringComparer.Ordinal);
        return retained.Select(id => rows.TryGetValue(id, out var row) ? row
            : throw new InvalidDataException("retained recording " + kind + " is absent from current canonical composition: " + id)).ToArray();
    }
}
