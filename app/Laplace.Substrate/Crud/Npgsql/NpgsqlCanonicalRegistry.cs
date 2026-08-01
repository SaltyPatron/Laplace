using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The one <c>laplace.register_canonicals</c> call. Every ingest lane that mints its own
/// canonical relation/entity-type names (bulk decomposers via IngestRunner, and the
/// in-process Chess lanes that bootstrap outside it — engine service, live-game host, PGN
/// lab ingestor) had hand-copied the identical open-connection/bind-text-array/execute
/// block around this one statement. Built on <see cref="NpgsqlRead"/> rather than opening
/// its own connection, so this is the single place that idiom exists.
/// </summary>
public static class NpgsqlCanonicalRegistry
{
    public static Task RegisterCanonicalsAsync(
        NpgsqlDataSource dataSource, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        if (names.Count == 0) return Task.CompletedTask;
        return NpgsqlRead.ExecuteNonQueryAsync(
            dataSource,
            "SELECT laplace.register_canonicals(@names)",
            bind: p => p.Add(new NpgsqlParameter
            {
                ParameterName = "names",
                Value = names.ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            }),
            label: "register_canonicals",
            ct: ct);
    }
}
