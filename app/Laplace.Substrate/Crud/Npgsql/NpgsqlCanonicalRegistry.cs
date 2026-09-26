using global::Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Process-wide entry to <c>realize.register_canonicals</c>. Every writer submits name sets
/// here; datasource-scoped state drops names already registered before a connection is
/// opened. The command runs through <see cref="NpgsqlRead"/>.
/// </summary>
public static class NpgsqlCanonicalRegistry
{
    private static readonly ConditionalWeakTable<NpgsqlDataSource, RegistrationState> States = new();

    /// <summary>
    /// Registers a set of canonical names once per datasource lifetime. Names enter the
    /// process cache only after the set statement succeeds, so the database stays
    /// authoritative.
    ///
    /// The registry is a readback dictionary for governed canonical keys and labels, not a
    /// payload store: values that look like JSON, contain line breaks or NUL, or parse as
    /// numbers are dropped here, since that content is composed in the tier spine and
    /// realized from its content root.
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
            .Where(static name => IsCanonicalRegistryValue(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
            return Task.FromResult(CanonicalRegistrationResult.Empty);

        return States.GetValue(dataSource, static _ => new RegistrationState())
            .RegisterAsync(dataSource, normalized, ct);
    }

    private static bool IsCanonicalRegistryValue(string value)
    {
        if (value.Length == 0) return false;
        char first = value[0];
        if (first is '{' or '[' or '"') return false;
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0) return false;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return false;
        return true;
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