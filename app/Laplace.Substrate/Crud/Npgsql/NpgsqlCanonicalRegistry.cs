using global::Npgsql;
using NpgsqlTypes;
using System.Runtime.CompilerServices;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The one process-wide authority for <c>realize.register_canonicals</c>. Bulk decomposers,
/// CLI closeout and in-process Chess lanes all submit sets here; the datasource-scoped state
/// collapses repeated sets before a connection is opened. Built on <see cref="NpgsqlRead"/>
/// so the command and its pool ownership also have one implementation.
/// </summary>
public static class NpgsqlCanonicalRegistry
{
    private static readonly ConditionalWeakTable<NpgsqlDataSource, RegistrationState> States = new();

    /// <summary>
    /// Registers a set of canonical names once for the lifetime of a datasource. All writers,
    /// decomposers and live ingest hosts sharing that datasource share this authority, so a
    /// completed file, the CLI closeout and a parallel sibling cannot each repeat the same SQL.
    /// The database remains authoritative: names enter the process cache only after the set
    /// statement succeeds.
    /// </summary>
    public static Task<CanonicalRegistrationResult> RegisterCanonicalsAsync(
        NpgsqlDataSource dataSource, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
            return Task.FromResult(CanonicalRegistrationResult.Empty);

        string[] normalized = names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
            return Task.FromResult(CanonicalRegistrationResult.Empty);

        return States.GetValue(dataSource, static _ => new RegistrationState())
            .RegisterAsync(dataSource, normalized, ct);
    }

    private sealed class RegistrationState
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly HashSet<string> _registered = new(StringComparer.Ordinal);

        public async Task<CanonicalRegistrationResult> RegisterAsync(
            NpgsqlDataSource dataSource, string[] normalized, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Filter after taking the gate. A concurrent caller may have persisted this
                // exact set while we waited; it then costs no connection and no SQL command.
                string[] missing = normalized.Where(name => !_registered.Contains(name)).ToArray();
                if (missing.Length == 0)
                    return new CanonicalRegistrationResult(normalized.Length, 0, 0, 0);

                long inserted = await NpgsqlRead.ExecuteScalarAsync<long>(
                    dataSource,
                    "SELECT realize.register_canonicals(@names)",
                    bind: p => p.Add(new NpgsqlParameter
                    {
                        ParameterName = "names",
                        Value = missing,
                        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                    }),
                    label: "register_canonicals",
                    ct: ct).ConfigureAwait(false);

                foreach (string name in missing) _registered.Add(name);
                return new CanonicalRegistrationResult(
                    normalized.Length, missing.Length, inserted, 1);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

public readonly record struct CanonicalRegistrationResult(
    int Requested, int Submitted, long Inserted, int RoundTrips)
{
    public static readonly CanonicalRegistrationResult Empty = new(0, 0, 0, 0);
}
